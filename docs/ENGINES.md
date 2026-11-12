# Conversion Engines

Filee uses an ultra-lean pipeline consisting of **9 core conversion engines** optimized for speed, reliability, and low resource usage.

---

## The 9 Core Engines

| Engine | Implementation / Library | Handles |
|---|---|---|
| **SkiaImageConverter** | `SkiaSharp` (native Google Skia) | Fast, high-quality raster conversion between **PNG, JPG/JPEG, WEBP, BMP, GIF, and ICO** with cubic resampling (Mitchell filter), full 8-direction EXIF orientation correction, and transparent canvas clearing. |
| **VectorConverter** | `Svg.Skia`, `SkiaSharp` | **SVG and SVGZ → PDF (vector), PNG, and JPG**. Preserves crisp vector curves and transparent backgrounds. |
| **PdfSharpConverter** | `PDFsharp` 6.2 | Direct PDF manipulation: **merging multiple documents, splitting pages, rotation, and compression**. |
| **PdfiumConverter** | `PDFtoImage` (Google PDFium) | High-speed PDF page rasterization: **PDF → PNG and JPG images**. |
| **PdfTextConverter** | `PdfPig` | Fast structural text and content extraction: **PDF → Plain Text (`.txt`) and Markdown (`.md`)** preserving headings and reading order without requiring OCR runtimes. |
| **DocxWriterConverter** | Internal OpenXML builder | High-performance generation of Word documents: **Markdown (`.md`), Plain Text, and structural tables → DOCX**. |
| **MarkdownConverter** | `Markdig` | Document parsing and sanitization: **Markdown → HTML and clean UTF-8 text**. |
| **SpreadsheetConverter** | `ExcelDataReader` | Spreadsheet data extraction: **Excel Workbook (`.xlsx`, `.xls`), OpenDocument (`.ods`), and CSV/TSV ↔ CSV and Excel (`.xlsx`)**. |
| **DataConverter** | `System.Text.Json`, `YamlDotNet`, `Tomlyn`, `System.Xml` | Structured data interchange: **JSON, JSON Lines, YAML, TOML, XML, and CSV tables ↔ each other**. |

---

## Architectural Changes (Pruned Engines)

To achieve zero background RAM and instant startup, Filee pruned 27 niche and heavy dependencies:
- **Magick.NET replaced by SkiaSharp:** Eliminated the heavy ~40 MB native ImageMagick C++ wrapper in favor of lightweight SkiaSharp.
- **Removed Heavy Optional Runtimes:** LibreOffice (~420 MB), Pandoc (~42 MB), Ghostscript, Calibre, and Whisper speech models have been completely removed.
- **Removed Niche Formats:** CAD (`ACadSharp`), 3D meshes (`SharpGLTF`), Hancom HWP/HWPX converters (`Unhwp`, `rhwp`), Archives (`7-Zip`), Subtitles, Fonts, Geospatial GIS, and Email formats (`MimeKit`, `MsgReader`) were pruned to maximize focus on student, developer, and office productivity.

---

## Automatic Route Planning

Filee automatically finds multi-step conversion paths when a direct 1-step converter is not available. For example:
- `data.xlsx` → `CSV` (via `SpreadsheetConverter`) → `JSON` (via `DataConverter`)
- `notes.md` → AST → Word `.docx` (via `DocxWriterConverter`)
- `vector.svg` → Skia raster → `WebP` (via `VectorConverter` + `SkiaImageConverter`)

All routing is resolved deterministically at runtime through `ConverterCatalog.Plan()`.
