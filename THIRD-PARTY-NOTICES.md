# Third-party notices

Convertino is MIT-licensed. It builds on these projects (all used unmodified, via NuGet or as external tools):

| Component | License | Used for |
|---|---|---|
| [Magick.NET](https://github.com/dlemstra/Magick.NET) / ImageMagick | Apache-2.0 / ImageMagick License | image decoding, encoding and processing |
| [PdfPig](https://github.com/UglyToad/PdfPig) | Apache-2.0 | PDF text extraction |
| [PDFsharp](https://github.com/empira/PDFsharp) 6.2.x | MIT | PDF merge / split / rotate / metadata, images → PDF |
| [Markdig](https://github.com/xoofx/markdig) | BSD-2-Clause | Markdown → HTML |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.50.7 | MIT | reading 7Z, RAR, ZIP, BZ2, XZ archives; writing 7Z |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) (+ DirectML) | MIT | running the cut-out model |
| [BiRefNet](https://github.com/ZhengPeng7/BiRefNet) — `BiRefNet_lite` ONNX export by [onnx-community](https://huggingface.co/onnx-community/BiRefNet_lite-ONNX) | MIT | background removal model (downloaded on request, not redistributed) |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT | Linux and macOS user interface |
| [Inter](https://github.com/rsms/inter) (via Avalonia.Fonts.Inter) | OFL-1.1 | Linux / macOS UI font |
| [Catppuccin](https://github.com/catppuccin/catppuccin) palette | MIT | colour themes |
| [Feather icons](https://github.com/feathericons/feather) (style reference, redrawn) | MIT | Linux / macOS tool icons |

External programs Convertino calls when present (not bundled): **ffmpeg** (LGPL/GPL), **poppler-utils** (GPL), **Tesseract** (Apache-2.0), **LibreOffice** (MPL-2.0), **Microsoft Office** (proprietary, user-installed).

On Windows, Segoe Fluent Icons and Segoe UI Variable are system fonts provided by the operating system. On macOS, PDF rendering (CoreGraphics), text recognition (Vision) and thumbnails (Quick Look) are system frameworks.
