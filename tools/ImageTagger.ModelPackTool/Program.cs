using ImageTagger.ModelPackTool;

// Model Pack development tool: pack source assets into the standard layout, or validate a pack/.itmodel.
// Input paths come from the command line only; no machine-specific paths are stored in the repo.
return args switch
{
    ["pack", ..] => Pack(args),
    ["validate", ..] => Validate(args),
    _ => Usage(),
};

static int Pack(string[] args)
{
    string? source = null, output = null, itmodel = null, translations = null;
    string id = "wd-eva02-tagger-2026-canary";
    double threshold = 0.6094;
    for (int i = 1; i < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--source": source = args[i + 1]; break;
            case "--output": output = args[i + 1]; break;
            case "--itmodel": itmodel = args[i + 1]; break;
            case "--id": id = args[i + 1]; break;
            case "--threshold": threshold = double.Parse(args[i + 1]); break;
            case "--translations": translations = args[i + 1]; break;
            default:
                Console.Error.WriteLine($"unknown option: {args[i]}");
                return 2;
        }
    }
    if (source is null || output is null)
    {
        Console.Error.WriteLine("usage: pack --source <dir> --output <dir> [--itmodel <file>] [--id <id>] [--threshold <0..1>] [--translations <file>]");
        return 2;
    }
    return PackCommand.Run(source, output, itmodel, id, threshold, translations);
}

static int Validate(string[] args)
{
    string? pack = null;
    for (int i = 1; i < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--pack": pack = args[i + 1]; break;
            default:
                Console.Error.WriteLine($"unknown option: {args[i]}");
                return 2;
        }
    }
    if (pack is null)
    {
        Console.Error.WriteLine("usage: validate --pack <dir|.itmodel>");
        return 2;
    }
    return ValidateCommand.Run(pack);
}

static int Usage()
{
    Console.WriteLine("usage: ImageTagger.ModelPackTool <pack|validate> [options]");
    Console.WriteLine("  pack     --source <dir> --output <dir> [--itmodel <file>] [--id <id>] [--threshold <0..1>] [--translations <file>]");
    Console.WriteLine("  validate --pack <dir|.itmodel>");
    return 2;
}
