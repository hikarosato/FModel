using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Engine.Font;
using FModel.Services;
using Org.BouncyCastle.Utilities;
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

        MessageBox.Show(
            $"Непідтримуваний тип файлу для перегляду шрифту: .{ext}",
            "Font Preview", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static void OpenFromRawFile(GameFile file)
    {
        try
        {
            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
            var bytes = provider.SaveAsset(file);

            int fontOffset = FindFontOffset(bytes);
            if (fontOffset < 0)
            {
                MessageBox.Show(
                    "Файл не є TTF/OTF за сигнатурою. Перегляд неможливий.",
                    "Font Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show($"Помилка при завантаженні файлу:\n{ex.Message}\n\n{ex.StackTrace}",
                "Font Preview", MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show("Не вдалося завантажити UE-пакет.", "Font Preview",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
                                MessageBox.Show(
                                    "UFontFace знайдено, але дані шрифту відсутні (можливо не inline).",
                                    "Font Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                        MessageBox.Show(
                            "Це растровий UFont (атлас текстур). Векторних даних немає — " +
                            "перегляд окремих гліфів недоступний.",
                            "Font Preview", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                }
            }

            MessageBox.Show(
                "У пакеті не знайдено об'єктів типу UFontFace або UFont.",
                "Font Preview", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Помилка при завантаженні пакету:\n{ex.Message}",
                "Font Preview", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

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