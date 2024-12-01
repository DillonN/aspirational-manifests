namespace Aspirate.Commands.Options;

public sealed class ChartNameOption : BaseOption<string?>
{
    private static readonly string[] _aliases = ["--chart-name"];

    private ChartNameOption() : base(_aliases, "ASPIRATE_CHART_NAME", null)
    {
        Name = nameof(IGenerateOptions.ChartName);
        Description = "Sets generated helm chart name";
        Arity = ArgumentArity.ZeroOrOne;
        IsRequired = false;
    }

    public static ChartNameOption Instance { get; } = new();
}
