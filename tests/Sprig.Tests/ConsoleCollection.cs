namespace Sprig.Tests;

/// <summary>Tests that swap process-wide state (Console.Out, SPRIG_STORE) run one at a time.</summary>
[CollectionDefinition("cli-console", DisableParallelization = true)]
public sealed class ConsoleCollection;
