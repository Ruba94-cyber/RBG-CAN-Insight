using RbgCanInsight.PerfBench;

// Synthetic, deterministic trace generator + headless benchmark stages.
//   gen <folder> <frames>
//   run <trace> <expected.json> <bench.dbc> <result.json> [--stages=import,verify,stats,dbc,uds,seek,timeline] [--guard-mb=2048]
// Driver: run-bench.ps1. Never touches the GUI.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: gen <folder> <frames> | run <trace> <expected.json> <dbc> <result.json> [--stages=..] [--guard-mb=N]");
    return 2;
}

switch (args[0])
{
    case "gen":
        return Gen.Run(args[1], long.Parse(args[2]));
    case "run":
        var stages = "import,verify,stats,dbc,uds,seek,timeline";
        long guard = 2048;
        foreach (var a in args.Skip(5))
        {
            if (a.StartsWith("--stages=")) stages = a["--stages=".Length..];
            else if (a.StartsWith("--guard-mb=")) guard = long.Parse(a["--guard-mb=".Length..]);
        }
        return await RunStages.Run(args[1], args[2], args[3], args[4], stages.Split(','), guard * 1048576);
    default:
        Console.Error.WriteLine("unknown command");
        return 2;
}
