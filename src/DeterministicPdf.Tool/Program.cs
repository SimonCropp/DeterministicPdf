return await new CommandLineApplicationBuilder()
    .AddCommandsFromThisAssembly()
    .SetExecutableName("detpdf")
    .SetTitle("DeterministicPdf CLI")
    .SetDescription("Neutralizes the fields of a PDF that change on every render, so the same source document always produces byte-identical output.")
    .Build()
    .RunAsync();
