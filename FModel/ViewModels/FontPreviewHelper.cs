using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Engine.Font;
using FModel.Services;
using System;
using System.Linq;
using System.Windows;

namespace FModel.Views;

public static class FontPreviewHelper
{
    private static readonly string[] RawFontExtensions = ["ttf", "otf", "ufont"];

    public static void OpenPreview(GameFile file)
    {
        if (file is null) return;

        var ext = file.Extension.ToLowerInvariant();

        if (RawFontExtensions.Contains(ext) && !file.IsUePackage)
        {
            OpenFromRawFile(file);
            return;
        }

        if (file.IsUePackage)
        {
            OpenFromUePackage(file);
            return;
        }

        MessageBox.Show($"Not supported file type: .{ext}", "Font Preview", MessageBoxButton.OK);
    }

    private static void OpenFromRawFile(GameFile file)
    {
        try
        {
            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
            var bytes = provider.SaveAsset(file);

            int fontOffset = FindFontOffset(bytes);
            if (fontOffset < 0)
            {
                MessageBox.Show("File is not a TTF/OTF by signature. Preview is not possible.", "Font Preview", MessageBoxButton.OK);
                return;
            }

            if (fontOffset > 0)
                bytes = bytes[fontOffset..];

            var displayName = file.NameWithoutExtension;
            var sourceType = file.Extension.ToUpperInvariant();

            var win = Helper.GetWindow<FontPreviewWindow>("Font Preview", () => FontPreviewWindow.GetOrCreate().Show());
            win.AddOrActivateFont(displayName, bytes, sourceType);
            win.FocusWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error occurred while loading the file:\n{ex.Message}\n{ex.StackTrace}", "Font Preview", MessageBoxButton.OK);
        }
    }

    private static void OpenFromUePackage(GameFile file)
    {
        try
        {
            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
            var pkg = provider.LoadPackage(file);
            if (pkg is null)
            {
                MessageBox.Show("Failed to load UE package.", "Font Preview", MessageBoxButton.OK);
                return;
            }

            foreach (var export in pkg.GetExports())
            {
                switch (export)
                {
                    case UFontFace fontFace:
                        {
                            var data = fontFace.FontFaceData?.Data;
                            if (data is null || data.Length == 0)
                            {
                                MessageBox.Show("UFontFace found, but font data is missing (possibly not inline).", "Font Preview", MessageBoxButton.OK);
                                return;
                            }

                            int fontOffset = FindFontOffset(data);
                            if (fontOffset > 0)
                                data = data[fontOffset..];

                            var displayName = file.NameWithoutExtension;
                            var win = Helper.GetWindow<FontPreviewWindow>("Font Preview", () => FontPreviewWindow.GetOrCreate().Show());
                            win.AddOrActivateFont(displayName, data, "UFontFace");
                            win.FocusWindow();

                            return;
                        }

                    case UFont:
                        MessageBox.Show("This is a raster UFont (texture atlas). No vector data available — preview of individual glyphs is not possible.", "Font Preview", MessageBoxButton.OK);
                        return;
                }
            }

            MessageBox.Show("No UFontFace or UFont objects found in the package.", "Font Preview", MessageBoxButton.OK);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error occurred while loading the package:\n{ex.Message}\n{ex.StackTrace}", "Font Preview", MessageBoxButton.OK);
        }
    }

    private static int FindFontOffset(byte[] data)
    {
        int limit = Math.Min(64, data.Length - 4);
        for (int i = 0; i <= limit; i++)
        {
            if (IsKnownFontSignatureAt(data, i))
                return i;
        }
        return -1;
    }

    private static bool IsKnownFontSignatureAt(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return false;

        if (data[offset] == 0x00 && data[offset + 1] == 0x01 &&
            data[offset + 2] == 0x00 && data[offset + 3] == 0x00) return true;

        if (data[offset] == 0x74 && data[offset + 1] == 0x72 &&
            data[offset + 2] == 0x75 && data[offset + 3] == 0x65) return true;

        if (data[offset] == 0x4F && data[offset + 1] == 0x54 &&
            data[offset + 2] == 0x54 && data[offset + 3] == 0x4F) return true;

        return false;
    }
}