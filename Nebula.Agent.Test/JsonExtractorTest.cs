namespace Nebula.Agent.Test;

public class JsonExtractorTest
{
    [Fact]
    public void ExtractJsonObject_WithSimpleJson_ShouldExtractCorrectly()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text before {\"key\": \"value\"} and text after";
        var expected = "{\"key\": \"value\"}";

        // Act
        var result = extractor.ExtractJsonObject(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractJsonObject_WithNestedJson_ShouldExtractCorrectly()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Before {\"outer\": {\"inner\": \"value\"}} after";
        var expected = "{\"outer\": {\"inner\": \"value\"}}";

        // Act
        var result = extractor.ExtractJsonObject(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractJsonObject_WithJsonAtStart_ShouldExtractCorrectly()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "{\"key\": \"value\"} some text after";
        var expected = "{\"key\": \"value\"}";

        // Act
        var result = extractor.ExtractJsonObject(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractJsonObject_WithJsonAtEnd_ShouldExtractCorrectly()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text before {\"key\": \"value\"}";
        var expected = "{\"key\": \"value\"}";

        // Act
        var result = extractor.ExtractJsonObject(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractJsonObject_WithOnlyJson_ShouldExtractCorrectly()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "{\"key\": \"value\"}";
        var expected = "{\"key\": \"value\"}";

        // Act
        var result = extractor.ExtractJsonObject(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractJsonObject_WithMultipleJsonObjects_ShouldExtractFromFirstToLast()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Text {\"first\": 1} middle {\"second\": 2} end";
        var expected = "{\"first\": 1} middle {\"second\": 2}";

        // Act
        var result = extractor.ExtractJsonObject(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractJsonObject_WithNoJson_ShouldThrowArgumentException()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text without json";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => extractor.ExtractJsonObject(input));
        Assert.Contains("No valid JSON object found", ex.Message);
    }

    [Fact]
    public void ExtractJsonObject_WithOnlyOpenBrace_ShouldThrowArgumentException()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text {";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => extractor.ExtractJsonObject(input));
        Assert.Contains("No valid JSON object found", ex.Message);
    }

    [Fact]
    public void ExtractJsonObject_WithOnlyCloseBrace_ShouldThrowArgumentException()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text }";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => extractor.ExtractJsonObject(input));
        Assert.Contains("No valid JSON object found", ex.Message);
    }

    [Fact]
    public void ExtractJsonObject_WithCloseBeforeOpen_ShouldThrowArgumentException()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text } before {";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => extractor.ExtractJsonObject(input));
        Assert.Contains("No valid JSON object found", ex.Message);
    }

    [Fact]
    public void ExtractJsonObject_WithEmptyBraces_ShouldExtractCorrectly()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Text before {} text after";
        var expected = "{}";

        // Act
        var result = extractor.ExtractJsonObject(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractJsonObject_WithComplexNestedStructure_ShouldExtractCorrectly()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = """Before {"Steps": [{"Id": 1, "Objective": "test", "Run": "cmd"}]} after""";
        var expected = """{"Steps": [{"Id": 1, "Objective": "test", "Run": "cmd"}]}""";

        // Act
        var result = extractor.ExtractJsonObject(input);

        // Assert
        Assert.Equal(expected, result);
    }
}
