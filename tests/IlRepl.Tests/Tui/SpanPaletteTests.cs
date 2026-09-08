using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// The palette has a colour for a light ground wherever it has one for a dark ground.
/// </summary>
[TestClass]
public sealed class SpanPaletteTests
{
    /// <summary>
    /// Every style coloured on a dark ground is coloured on a light ground too, differently, and
    /// the styles left to the terminal's own foreground are left alone on both.
    /// </summary>
    [TestMethod]
    public void LightColor_CoversEveryColouredStyle()
    {
        foreach (var style in Enum.GetValues<SpanStyle>())
        {
            var dark = SpanPalette.Color(style);
            var light = SpanPalette.LightColor(style);
            Assert.AreEqual(dark.IsDefault, light.IsDefault, $"{style} should be coloured on both grounds or on neither");
            if (!dark.IsDefault)
            {
                Assert.AreNotEqual(dark, light, $"{style} should not reuse its dark colour on a light ground");
            }
        }
    }
}
