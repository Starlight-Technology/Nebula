using Corona.Theming;

namespace Nebula.App.Test;

public sealed class CoronaThemeCompatibilityTest
{
    [Fact]
    public void with_overrides_must_apply_primitive_and_semantic_values()
    {
        var baseTheme = CoronaThemes.Light();
        var overrides = new CoronaThemeOverrides(
            Primitive: new CoronaPrimitiveTokenOverrides(
                Colors: new CoronaColorPrimitives(Brand600: "#111111"),
                Typography: new CoronaTypographyPrimitives(FontFamily: "Test Font")),
            Semantic: new CoronaSemanticTokenOverrides(
                SurfaceBackground: "#222222",
                FontSizeBody: "13px"));

        var theme = baseTheme.WithOverrides(overrides);

        Assert.Equal("#111111", theme.Primitive.Colors.Brand600);
        Assert.Equal("#222222", theme.Semantic.SurfaceBackground);
        Assert.Equal("13px", theme.Semantic.FontSizeBody);
        Assert.Equal("Test Font", theme.Semantic.FontFamilyDefault);
    }
}
