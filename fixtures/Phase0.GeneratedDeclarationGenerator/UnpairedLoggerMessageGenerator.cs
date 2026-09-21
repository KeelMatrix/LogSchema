using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Phase0.GeneratedDeclarationGenerator;

[Generator]
public sealed class UnpairedLoggerMessageGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static output => output.AddSource(
            "Unpaired.LoggerMessage.g.cs",
            SourceText.From("""
                using Microsoft.Extensions.Logging;

                namespace Phase0.Pairing;

                public static partial class UnpairedGeneratedLogging
                {
                    [LoggerMessage(EventId = 1299, Level = LogLevel.Information, Message = "Unpaired generated declaration {Value}")]
                    public static partial void UnpairedGenerated(ILogger logger, string value);
                }
                """, Encoding.UTF8)));
    }
}
