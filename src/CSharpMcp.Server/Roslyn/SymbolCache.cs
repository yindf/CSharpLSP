using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace CSharpMcp.Server.Roslyn;

public class SymbolCache
{
    private SymbolTable<ISymbol> _symbolTable = new SymbolTable<ISymbol>();

    public SymbolCache()
    {
    }

    public void AddSymbol(ISymbol symbol)
    {
        _symbolTable.Add(symbol.Name, symbol);
    }

    public void Clear()
    {
        _symbolTable.Clear();
    }

    public IEnumerable<ISymbol> Search(string query)
    {
        return _symbolTable.Search(query);
    }

    public async Task AddSolutionAsync(Solution solution, CancellationToken cancellationToken = default)
    {
        foreach (var project in solution.Projects)
        {
            await AddProjectAsync(project, cancellationToken);
        }
    }

    public async Task AddProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation != null)
        {
            foreach (var symbol in compilation.GetSymbolsWithName(s => true, SymbolFilter.All))
            {
                _symbolTable.Add(symbol.Name, symbol);
            }
        }
    }

    /// <summary>
    /// High-performance symbol table for compilation scenarios
    /// Supports * (zero or more) and ? (single char) wildcards
    /// Optimized for 10K-100K symbols with O(m) average lookup for exact matches
    /// </summary>
    public class SymbolTable<T> where T : class
    {
        private TrieNode _root = new();
        private int _count;

        private class TrieNode
        {
            // Use array for ASCII letters (A-Z, a-z, _, 0-9) - common in code symbols
            // Dictionary for other chars to save memory
            public TrieNode[] FastMap = new TrieNode[63]; // [0-9]=0-9, [10-35]=A-Z, [36-61]=a-z, 62=_
            public Dictionary<char, TrieNode> SlowMap;

            // Store values at this node (multiple symbols can end here via different paths? No, but for completeness)
            public List<T> Values = new();
            public string SymbolName; // Full symbol name for exact match retrieval

            // For wildcard optimization: cache if this subtree contains matches
            public bool HasValuesInSubtree;
        }

        // Char to index mapping for fast array access
        private static int CharToIndex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0'; // 0-9
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10; // 10-35
            if (c >= 'a' && c <= 'z') return c - 'a' + 36; // 36-61
            if (c == '_') return 62; // 62
            return -1; // Requires slow map
        }

        public int Count => _count;

        /// <summary>
        /// Add a symbol with its associated value
        /// </summary>
        public void Add(string symbol, T value)
        {
            if (string.IsNullOrEmpty(symbol))
                throw new ArgumentException("Symbol cannot be null or empty", nameof(symbol));

            var node = _root;

            foreach (var c in symbol)
            {
                node.HasValuesInSubtree = true;
                var idx = CharToIndex(c);

                if (idx >= 0)
                {
                    // Fast path: ASCII identifier char
                    node.FastMap[idx] ??= new TrieNode();
                    node = node.FastMap[idx];
                }
                else
                {
                    // Slow path: special characters
                    node.SlowMap ??= new Dictionary<char, TrieNode>();
                    if (!node.SlowMap.TryGetValue(c, out var next))
                    {
                        next = new TrieNode();
                        node.SlowMap[c] = next;
                    }

                    node = next;
                }
            }

            if (node.SymbolName == null)
                _count++;

            node.SymbolName = symbol;
            node.Values.Add(value);
            node.HasValuesInSubtree = true;
        }

        /// <summary>
        /// Exact match lookup - O(m) where m is symbol length
        /// </summary>
        public IReadOnlyList<T> GetExact(string symbol)
        {
            var node = _root;

            foreach (var c in symbol)
            {
                var idx = CharToIndex(c);
                node = idx >= 0 ? node.FastMap[idx] : node.SlowMap?.GetValueOrDefault(c);

                if (node == null)
                    return [];
            }

            return node?.Values ?? [];
        }

        /// <summary>
        /// Wildcard search: supports * (zero or more chars) and ? (single char)
        /// Optimized to prune branches that cannot contain matches
        /// </summary>
        public IEnumerable<T> Search(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return Array.Empty<T>();

            // Optimization: if no wildcards, use exact lookup
            if (!pattern.Contains('*') && !pattern.Contains('?'))
                return GetExact(pattern);

            return SearchRecursive(_root, pattern, 0, new HashSet<TrieNode>());
        }

        private IEnumerable<T> SearchRecursive(TrieNode node, string pattern, int pos, HashSet<TrieNode> visited)
        {
            // Pruning: if this subtree has no values, skip
            if (!node.HasValuesInSubtree)
                yield break;

            // Prevent cycles on * patterns
            if (!visited.Add(node))
                yield break;

            if (pos >= pattern.Length)
            {
                // End of pattern - return all values at this node
                foreach (var v in node.Values)
                    yield return v;
                yield break;
            }

            var c = pattern[pos];

            if (c == '*')
            {
                // * matches zero characters: skip to next pattern char
                foreach (var result in SearchRecursive(node, pattern, pos + 1, new HashSet<TrieNode>(visited)))
                    yield return result;

                // * matches one or more: try all children
                foreach (var child in GetAllChildren(node))
                {
                    // Continue with same * (matches more chars) or advance (matches done)
                    foreach (var result in SearchRecursive(child, pattern, pos, new HashSet<TrieNode>(visited)))
                        yield return result;
                }
            }
            else if (c == '?')
            {
                // ? matches exactly one character: must go to a child
                foreach (var child in GetAllChildren(node))
                {
                    foreach (var result in SearchRecursive(child, pattern, pos + 1, new HashSet<TrieNode>(visited)))
                        yield return result;
                }
            }
            else
            {
                // Exact character match required
                var next = GetChild(node, c);
                if (next != null)
                {
                    foreach (var result in SearchRecursive(next, pattern, pos + 1, new HashSet<TrieNode>(visited)))
                        yield return result;
                }
            }
        }

        private static TrieNode GetChild(TrieNode node, char c)
        {
            var idx = CharToIndex(c);
            return idx >= 0 ? node.FastMap[idx] : node.SlowMap?.GetValueOrDefault(c);
        }

        private static IEnumerable<TrieNode> GetAllChildren(TrieNode node)
        {
            // Fast map
            foreach (var child in node.FastMap)
                if (child != null)
                    yield return child;

            // Slow map
            if (node.SlowMap != null)
                foreach (var child in node.SlowMap.Values)
                    yield return child;
        }

        /// <summary>
        /// Get all symbols (for debugging/introspection)
        /// </summary>
        public IEnumerable<(string Symbol, IReadOnlyList<T> Values)> GetAllSymbols()
        {
            return Traverse(_root);
        }

        private IEnumerable<(string, IReadOnlyList<T>)> Traverse(TrieNode node)
        {
            if (node.SymbolName != null)
                yield return (node.SymbolName, node.Values);

            foreach (var child in GetAllChildren(node))
            foreach (var result in Traverse(child))
                yield return result;
        }

        public void Clear()
        {
            _root = new TrieNode();
        }
    }
}

