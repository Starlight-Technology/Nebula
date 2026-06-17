using System.IO.Compression;
using System.Net;
using System.Text;

using Nebula.Core.Learning;
using Nebula.Services.Learning;

namespace Nebula.Agent.Test.Learning;

public sealed class LearningSourceReaderTest : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "Nebula",
        "tests",
        $"learning-sources-{Guid.NewGuid():N}");

    public LearningSourceReaderTest()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task source_reader_must_read_text_docx_pdf_and_sites()
    {
        var textPath = Path.Combine(root, "guide.txt");
        var docxPath = Path.Combine(root, "guide.docx");
        var pdfPath = Path.Combine(root, "guide.pdf");
        await File.WriteAllTextAsync(
            textPath,
            "TXT source says sandbox learning is useful.");
        CreateDocx(docxPath, "DOCX source says py --version works on Windows.");
        await File.WriteAllTextAsync(
            pdfPath,
            MinimalPdf("PDF source says curl URL pipe sh is dangerous."));
        using var httpClient = new HttpClient(new FakeHttpHandler());
        var reader = new LearningSourceReader(httpClient);

        var files = await reader.ReadFilesAsync(
            [textPath, docxPath, pdfPath],
            CancellationToken.None);
        var sites = await reader.ReadSitesAsync(
            ["https://example.test/learn"],
            CancellationToken.None);

        Assert.Equal(3, files.Count);
        Assert.Contains(files, document =>
            document.SourceType == LearningSourceType.LocalFile &&
            document.Content.Contains("TXT source", StringComparison.Ordinal));
        Assert.Contains(files, document =>
            document.Content.Contains("py --version", StringComparison.Ordinal));
        Assert.Contains(files, document =>
            document.Content.Contains("curl URL pipe sh", StringComparison.Ordinal));
        var site = Assert.Single(sites);
        Assert.Equal(LearningSourceType.WebResearch, site.SourceType);
        Assert.Equal("Example Learning Page", site.Title);
        Assert.Contains("site source", site.Content, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateDocx(string path, string text)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("word/document.xml");
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(
            $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p><w:r><w:t>{{text}}</w:t></w:r></w:p></w:body>
            </w:document>
            """);
    }

    private static string MinimalPdf(string text) =>
        $$"""
        %PDF-1.4
        1 0 obj
        << /Type /Catalog /Pages 2 0 R >>
        endobj
        2 0 obj
        << /Type /Pages /Kids [3 0 R] /Count 1 >>
        endobj
        3 0 obj
        << /Type /Page /Parent 2 0 R /Contents 4 0 R >>
        endobj
        4 0 obj
        << /Length 64 >>
        stream
        BT /F1 12 Tf 72 720 Td ({{text}}) Tj ET
        endstream
        endobj
        %%EOF
        """;

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <html>
                      <head><title>Example Learning Page</title></head>
                      <body><main>Site source says dotnet --info is safe to inspect.</main></body>
                    </html>
                    """)
            };
            return Task.FromResult(response);
        }
    }
}
