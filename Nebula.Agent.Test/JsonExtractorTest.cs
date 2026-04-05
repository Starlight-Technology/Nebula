namespace Nebula.Agent.Test;

public class JsonExtractorTest
{
    [Fact]
    public void extract_json_object_must_return_json_when_input_contains_simple_json()
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
    public void extract_json_object_must_return_json_when_input_contains_nested_json()
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
    public void extract_json_object_must_return_json_when_input_starts_with_json()
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
    public void extract_json_object_must_return_json_when_input_ends_with_json()
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
    public void extract_json_object_must_return_json_when_input_only_contains_json()
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
    public void extract_json_object_must_return_outermost_json_when_input_contains_multiple_objects()
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
    public void extract_json_object_must_throw_argument_exception_when_input_contains_no_json()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text without json";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => extractor.ExtractJsonObject(input));
        Assert.Contains("No valid JSON object found", ex.Message);
    }

    [Fact]
    public void extract_json_object_must_throw_argument_exception_when_input_contains_only_open_brace()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text {";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => extractor.ExtractJsonObject(input));
        Assert.Contains("No valid JSON object found", ex.Message);
    }

    [Fact]
    public void extract_json_object_must_throw_argument_exception_when_input_contains_only_close_brace()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text }";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => extractor.ExtractJsonObject(input));
        Assert.Contains("No valid JSON object found", ex.Message);
    }

    [Fact]
    public void extract_json_object_must_throw_argument_exception_when_close_brace_comes_before_open_brace()
    {
        // Arrange
        var extractor = new JsonExtractor();
        var input = "Some text } before {";

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => extractor.ExtractJsonObject(input));
        Assert.Contains("No valid JSON object found", ex.Message);
    }

    [Fact]
    public void extract_json_object_must_return_empty_json_when_input_contains_empty_braces()
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
    public void extract_json_object_must_return_json_when_input_contains_complex_nested_structure()
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
