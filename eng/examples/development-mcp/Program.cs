using Cohesive.Semantics;
using DevelopmentMcp.Example;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: DevelopmentMcp.Example <repository-root> <output-bundle>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
// Select the source scope; the library owns discovery conventions and atomic publication.
var inputs = SemanticWorkspaceExport.DiscoverInputs(root,
    ["src/Cohesive", "src/Cohesive.Analyzers", "src/Cohesive.Transitions", "src/Cohesive.Semantics", "eng/examples/development-mcp"],
    ["Directory.Build.props", "nuget.config"]);
Console.WriteLine(SemanticWorkspaceExport.Write(root, Path.GetRelativePath(root, Path.GetFullPath(args[1])), inputs,
    typeof(OrderModel).Assembly.GetName().Version!.ToString(), revision => [OrderModel.Create(revision)]));
return 0;
