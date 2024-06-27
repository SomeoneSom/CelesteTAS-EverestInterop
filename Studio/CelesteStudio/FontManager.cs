using System.Linq;
using System.Reflection;
using Eto.Drawing;

namespace CelesteStudio;

public static class FontManager {
    public const string FontFamilyBuiltin = "<builtin>";
    public const string FontFamilyBuiltinDisplayName = "JetBrains Mono (builtin)";
    
    private static Font? editorFontRegular, editorFontBold, editorFontItalic, editorFontBoldItalic, statusFont;

    public static Font EditorFontRegular => editorFontRegular ??= CreateFont(Settings.Instance.FontFamily, Settings.Instance.EditorFontSize);
    public static Font EditorFontBold => editorFontBold ??= CreateFont(Settings.Instance.FontFamily, Settings.Instance.EditorFontSize, FontStyle.Bold);
    public static Font EditorFontItalic => editorFontItalic ??= CreateFont(Settings.Instance.FontFamily, Settings.Instance.EditorFontSize, FontStyle.Italic);
    public static Font EditorFontBoldItalic => editorFontBoldItalic ??= CreateFont(Settings.Instance.FontFamily, Settings.Instance.EditorFontSize, FontStyle.Bold | FontStyle.Italic);
    public static Font StatusFont => statusFont ??= CreateFont(Settings.Instance.FontFamily, Settings.Instance.StatusFontSize);
    
    private static FontFamily? builtinFontFamily;
    public static Font CreateFont(string fontFamily, float size, FontStyle style = FontStyle.None) {
        if (fontFamily == FontFamilyBuiltin) {
            var asm = Assembly.GetExecutingAssembly();
            builtinFontFamily ??= FontFamily.FromStreams(asm.GetManifestResourceNames()
                .Where(name => name.StartsWith("JetBrainsMono/"))
                .Select(name => asm.GetManifestResourceStream(name)));
            
            return new Font(builtinFontFamily, size, style);
        } else {
            return new Font(fontFamily, size, style);
        }
    }
    
    public static float LineHeight(this Font font) {
        if (Eto.Platform.Instance.IsWpf) {
            // WPF reports the line height a bit to small for some reason?
            return font.LineHeight + 5.0f;
        }

        return font.LineHeight;
    }
    public static SizeF MeasureText(this Font font, string text) {
        // Can't just use MeasureString, since WPF seems like it reports the smallest possible height?
        float width = font.MeasureString(text).Width;
        return new SizeF(width, font.LineHeight());
    }

    public static void OnFontChanged() {
        // Clear cached fonts
        editorFontRegular = editorFontBold = editorFontItalic = editorFontBoldItalic = statusFont = null;
    }
}
