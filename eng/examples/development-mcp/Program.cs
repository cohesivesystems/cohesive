using Cohesive.Semantics;
using DevelopmentMcp.Example;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: DevelopmentMcp.Example <repository-root> <output-bundle>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
// This source-build example includes framework sources and build inputs so framework changes invalidate it too.
var projects = new[] { "Cohesive", "Cohesive.Analyzers", "Cohesive.Transitions", "Cohesive.Semantics" };
var inputs = projects.SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*", SearchOption.AllDirectories))
    .Concat(Directory.EnumerateFiles(Path.Combine(root, "eng/examples/development-mcp")))
    .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
    .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".targets")
    .Select(path => Path.GetRelativePath(root, path))
    .Append("Directory.Build.props").Append("nuget.config");
var json = SemanticWorkspaceExport.Create(root, inputs,
    typeof(OrderModel).Assembly.GetName().Version!.ToString(), revision => [OrderModel.Create(revision)]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
    File.WriteAllText(temporary, json);
    File.Move(temporary, output, overwrite: true);
}
finally { if (File.Exists(temporary)) File.Delete(temporary); }
Console.WriteLine(output);
return 0;
