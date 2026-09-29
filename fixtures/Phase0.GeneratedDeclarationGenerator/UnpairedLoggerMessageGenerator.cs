using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Phase0.GeneratedDeclarationGenerator;

[Generator]
public sealed class UnpairedLoggerMessageGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var options = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => new GenerationOptions(
            ReadOption(provider.GlobalOptions, "LogSchemaGeneratedTreeCount", 1),
            ReadOption(provider.GlobalOptions, "LogSchemaGeneratedTreeSize", 0),
            !provider.GlobalOptions.TryGetValue("build_property.LogSchemaGeneratedTreeDeclarations", out var declarations) ||
            !string.Equals(declarations, "none", StringComparison.OrdinalIgnoreCase)));

        context.RegisterSourceOutput(options, static (productionContext, generation) =>
        {
            for (var index = 0; index < generation.Count; index++)
            {
                var padding = generation.Size > 0 ? new string('x', generation.Size) : string.Empty;
                var hint = generation.Count == 1
                    ? "Unpaired.LoggerMessage.g.cs"
                    : $"Unpaired.LoggerMessage.{index.ToString("D5", CultureInfo.InvariantCulture)}.g.cs";
                var declaration = generation.IncludeLoggerMessage
                    ? """
                        [LoggerMessage(EventId = 1299, Level = LogLevel.Information, Message = "Unpaired generated declaration {Value}")]
                        public static partial void UnpairedGenerated(ILogger logger, string value);
                        """
                    : "public static void UnpairedGenerated() { }";
                productionContext.AddSource(
                    hint,
                    SourceText.From($$"""
                        using Microsoft.Extensions.Logging;

                        namespace Phase0.Pairing;

                        public static partial class UnpairedGeneratedLogging{{index}}
                        {
                            {{declaration}}
                            // {{padding}}
                        }
                        """, Encoding.UTF8));
            }
        });
    }

    private static int ReadOption(AnalyzerConfigOptions options, string name, int fallback) =>
        options.TryGetValue("build_property." + name, out var value) &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
        parsed >= 0
            ? parsed
            : fallback;

    private sealed class GenerationOptions
    {
        internal GenerationOptions(int count, int size, bool includeLoggerMessage)
        {
            Count = count;
            Size = size;
            IncludeLoggerMessage = includeLoggerMessage;
        }

        internal int Count { get; }
        internal int Size { get; }
        internal bool IncludeLoggerMessage { get; }
    }
}
