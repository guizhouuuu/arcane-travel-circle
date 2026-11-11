using System.Reflection;
using System.Runtime.Loader;
if (args.Length != 3) return 2;
string game = args[0], mod = args[1], tests = args[2];
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    foreach (string root in new[] { mod, game, Path.Combine(game, "smapi-internal"), Path.GetDirectoryName(tests)! })
    {
        string file = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(file)) return ctx.LoadFromAssemblyPath(file);
    }
    return null;
};
try
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(tests);
    var run = assembly.GetType("MountainLandingRegression", true)!.GetMethod("Run")!;
    run.Invoke(null, new object[] { game, (Action<bool, string>)((ok, description) =>
    {
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + description);
        if (!ok) throw new InvalidOperationException(description);
    }) });
    return 0;
}
catch (Exception error)
{
    Console.WriteLine("Result: " + error.GetBaseException().Message);
    return 1;
}
