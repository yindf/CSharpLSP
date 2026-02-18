namespace CSharpMcp.Server.Models.Tools;

/// <summary>
/// Extension methods for parameter default value handling
/// </summary>
public static class ParameterExtensions
{
    /// <summary>
    /// Gets the max results value, ensuring a sensible default
    /// </summary>
    public static int GetMaxResults(this SearchSymbolsParams parameters) =>
        parameters.MaxResults > 0 ? parameters.MaxResults : 100;

    /// <summary>
    /// Gets the body max lines value, ensuring a sensible default
    /// </summary>
    public static int GetBodyMaxLines(this GetSymbolsParams parameters) =>
        parameters.BodyMaxLines > 0 ? parameters.BodyMaxLines : 100;

    public static int GetBodyMaxLines(this GoToDefinitionParams parameters) =>
        parameters.BodyMaxLines > 0 ? parameters.BodyMaxLines : 100;

    public static int GetBodyMaxLines(this ResolveSymbolParams parameters) =>
        parameters.BodyMaxLines > 0 ? parameters.BodyMaxLines : 50;

    public static int GetBodyMaxLines(this GetSymbolCompleteParams parameters) =>
        parameters.BodyMaxLines > 0 ? parameters.BodyMaxLines : 100;

    public static int GetBodyMaxLines(this BatchGetSymbolsParams parameters) =>
        parameters.BodyMaxLines > 0 ? parameters.BodyMaxLines : 50;

    public static int GetMaxDerivedDepth(this GetInheritanceHierarchyParams parameters) =>
        parameters.MaxDerivedDepth > 0 ? parameters.MaxDerivedDepth : 3;

    /// <summary>
    /// Gets the max references value, ensuring a sensible default
    /// </summary>
    public static int GetMaxReferences(this GetSymbolCompleteParams parameters) =>
        parameters.MaxReferences > 0 ? parameters.MaxReferences : 50;

    /// <summary>
    /// Gets the context lines value, ensuring a sensible default
    /// </summary>
    public static int GetContextLines(this FindReferencesParams parameters) =>
        parameters.ContextLines > 0 ? parameters.ContextLines : 3;
}
