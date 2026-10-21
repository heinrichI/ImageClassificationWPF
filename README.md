# Image Classification WPF

WPF MVVM application for image classification, sorting, and semantic search using modern CNN architectures and CLIP embeddings.

Built with **Dependency Inversion (DIP)** — all cross-project dependencies go through interfaces. Implementations are `internal`, exposed only via `ServiceCollectionExtensions`.

## Architecture

```
ImageClassification.ArchiveReader   — CBZ/CBR archive extraction (IArchiveReader, AddArchiveReader())
ImageClassification.VectorStore     — SQLite vector embedding cache (IVectorStore, AddVectorStore())
ImageClassification.Core            — Business logic, models, services (AddImageClassificationCore())
ImageClassification.UI              — WPF shell (MVVM + CommunityToolkit)
ImageClassification.Tests           — xUnit + Moq
```

Dependency flow: `UI → Core → (ArchiveReader, VectorStore)` — UI knows only interfaces, never concrete types.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- ONNX Runtime (included via NuGet)
- Models: download via UI (**Settings → Download Models**) or manually (see below)

## Build & Run

```bash
dotnet restore
dotnet build
dotnet run --project src/ImageClassification.UI
```

## Test

```bash
dotnet test src/ImageClassification.Tests
```

Integration tests (require real ONNX model files) are skipped by default. Run them manually:
```bash
dotnet test --filter "FullyQualifiedName~IntegrationTest"
```

## Features

### Training (Tab 1)
| Model | Top-1 ImageNet | Params | Size | Best for |
|---|---|---|---|---|
| **EfficientNet-B0** | 78.57% | 5.3M | 21 MB | Accuracy/size ratio |
| **EfficientNet-B1** | 80.40% | 7.8M | 31 MB | Default |
| **EfficientNetV2-S** | 83.90% | 21.5M | 86 MB | Max accuracy |
| **ConvNeXt-Tiny** | 82.07% | 28.6M | 114 MB | Modern CNN |
| **MobileNetV3-Large** | 75.51% | 5.5M | 22 MB | Fastest |

- Hyper-parameters: batch size, epochs, learning rate, weight decay
- Augmentation: TrivialAugment + MixUp + CutMix + Label Smoothing
- LR schedule: Cosine Annealing + 5-epoch warmup
- Transfer learning: progressive unfreezing (head → last blocks → full fine-tune)

### Evaluation (Tab 2)
- Load a trained `.onnx` model
- Evaluate on test set (subfolder-per-class)
- Per-class accuracy, precision, recall, F1

### Classify & Sort (Tab 3)
- Batch classify with confidence threshold
- Auto-sort images into class-named subfolders
- Progress bar + processing log

### Analyze (Tab 4)
- **Auto-Cluster**: k-means clustering without predefined labels (needs the MobileNetV2 model)
  - Auto-determine cluster count (silhouette score)
  - Editable cluster names
  - "Sort by Clusters" copies images into one subfolder per cluster
- **By Tags**: CLIP zero-shot tag generation (needs the CLIP ViT-B/32 model)
  - Multi-line candidate tag input
  - Top-K is chosen **only from your candidate list** — CLIP cannot invent labels,
    so include every topic you care about (e.g. `autumn`) plus contrasting tags
    (e.g. `winter, spring, summer`) for meaningful scores
  - Image preview with tag checkboxes; tags above the confidence threshold are pre-checked
  - Edit and filter tags before sorting
  - "Sort by Tags" copies each image into one subfolder per checked tag
- Only the top level of the source folder is scanned — images in subfolders are ignored
- Sort buttons **copy** (originals stay in place) and never clean target subfolders,
  so stale copies from previous runs may remain
- Cancel takes effect between images; By Tags keeps the partial results

### Comic Cover Search (Tab 6)
- Search CBZ/CBR comic archives by semantic description
- Source directories are added via the "Add..." button or by dragging folders from
  Windows Explorer onto the "Select comic archive directories" panel
- Uses CLIP ViT-B/32 ONNX model for text + image embeddings
- Results ranked by cosine similarity (raw CLIP score)
- Adjustable similarity threshold (default 0.25)
- Score distribution shown in status bar
- The in-tab "DB" menu maintains the vector cache: **Info** (sizes, entry counts, age),
  **Optimize** (fold the WAL into the database), **Vacuum** (rebuild the file, reclaim
  free space) and **Prune missing files** (remove entries for files that no longer
  exist on disk; files on disconnected drives are kept). Prune checks paths grouped by
  parent directory in parallel — a deleted directory makes all its paths missing at once,
  so even hundreds of thousands of entries are pruned in seconds; the removal itself is
  one bulk delete driven by a temp table of the missing paths (no per-chunk table
  re-scan), and every phase runs on background threads — the window and its Cancel
  button stay responsive throughout, with progress ticks at least every 500 ms. Vacuum and Prune run in a modal window
  with a progress bar and a Cancel/Interrupt button — cancelling (prune) or interrupting
  (vacuum) aborts the operation and leaves the database exactly as it was

### Search Image (Tab 7)
- Search plain image files (JPG/JPEG/PNG/BMP/TIFF/WebP) by semantic description
- Multiple source directories, scanned top-level or with subfolders
  ("Recurse subfolders", persisted); directories are added via the "Add..." button or
  by dragging folders from Windows Explorer onto the panel
- Uses CLIP ViT-B/32 ONNX model. Image encoding is **batched exactly like Comic Search**:
  the "ClipViTB32" per-file vector cache (one row per photo, keyed by file path +
  mtime + size) is probed in a single batched round-trip; cache hits are scored
  without reading the file, cache misses are encoded in GPU batches
  (Settings → GPU Batch Size, default 32) and saved back to the same cache — so a
  second search of the same directory is a near-instant cache probe, and embeddings
  computed by Analyze/By Tags are reused without re-encoding
- Results ranked by cosine similarity, adjustable threshold (default 25%)
- Directories, last 10 query phrases, threshold and the recursion option are
  persisted between app runs
- Tile grid with thumbnails loaded on demand; left click opens the image in the
  default viewer, right click → copy path; files that fail to decode are skipped
  and listed in the status line
- "Copy results images…" (tab menu) copies all result files into a chosen folder
- Same "DB" menu as Comic Search (Info / Optimize / Vacuum / Prune missing files),
  with a modal progress + Cancel/Interrupt window for the long operations
- Cancel takes effect between GPU batches; everything encoded so far is already scored
  into the results AND already saved to the per-file cache (chunk-by-chunk, exactly
  like Comic Search), so a cancelled run still warms the cache — the next search
  resumes from the cache instead of starting over

### Settings (Tab 5)
- ONNX Runtime thread count
- GPU batch size (images per ONNX inference batch)
- Thumbnail width / cache size, max result previews
- **GPU (CUDA / cuDNN)**: bin directories of CUDA and cuDNN, prepended to the process
  PATH at startup so the ONNX Runtime CUDA execution provider can find them. Empty
  values are auto-detected on the first launch (NVIDIA registry / typical install
  locations / shallow drive scan); "Detect & Save" and Browse let you override. Changes
  apply after a restart; if CUDA is configured but ONNX Runtime still can't load it,
  a "GPU not available" dialog offers the same path selection
- Built-in model downloader with progress bar

## Model Setup

### Built-in downloader (recommended)

Open **Settings → Download Models** and click "Download" for each model.

### Manual download

| Model | Source |
|---|---|
| EfficientNet-B0 | [ONNX Model Zoo](https://github.com/onnx/models/tree/main/validated/vision/classification/efficientnet-lite4) |
| MobileNetV2 | [ONNX Model Zoo](https://github.com/onnx/models/tree/main/validated/vision/classification/mobilenet) |
| CLIP ViT-B/32 | [Hugging Face](https://huggingface.co/openai/clip-vit-base-patch32) |

### PyTorch → ONNX export

```python
# pip install torch torchvision onnx transformers
import torch
import torchvision.models as models

model = models.efficientnet_b0(pretrained=True)
model.eval()

dummy = torch.randn(1, 3, 224, 224)
torch.onnx.export(model, dummy, "model.onnx",
    input_names=["input"], output_names=["output"],
    dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}},
    opset_version=17)
```

### CLIP ONNX export

```python
from transformers import CLIPModel

model = CLIPModel.from_pretrained("openai/clip-vit-base-patch32")
model.eval()

dummy_image = torch.randn(1, 3, 224, 224)
dummy_text = torch.randint(0, 49408, (1, 77))

torch.onnx.export(model, (dummy_image, dummy_text), "clip_vit_b32.onnx",
    input_names=["pixel_values", "input_ids"],
    output_names=["image_embeds", "text_embeds"],
    opset_version=17)
```

Models are stored at: `%APPDATA%/ImageClassification/models/` (Windows) or `./models/` (local).

## Tech Stack

| Component | Library |
|---|---|
| UI Framework | WPF (.NET 8) |
| MVVM | CommunityToolkit.Mvvm 8.4 |
| ML — training | TorchSharp 0.103 |
| ML — inference | Microsoft.ML.OnnxRuntime 1.17 |
| Image processing | SixLabors.ImageSharp 3.1 |
| Charts | LiveChartsCore.SkiaSharpView |
| DI | Microsoft.Extensions.DependencyInjection |
| Archive reader | SevenZipExtractor |
| Vector cache | Microsoft.Data.Sqlite |
| Unit tests | xUnit + Moq |

## Project Structure

```
src/
├── ImageClassification.ArchiveReader/
│   ├── IArchiveReader.cs            — public interface
│   ├── ArchiveReader.cs             — internal implementation
│   └── ServiceCollectionExtensions.cs — AddArchiveReader()
│
├── ImageClassification.VectorStore/
│   ├── IVectorStore.cs              — public interface
│   ├── SqliteVectorStore.cs         — internal implementation
│   └── ServiceCollectionExtensions.cs — AddVectorStore()
│
├── ImageClassification.Core/
│   ├── Models/                      — domain models & DTOs (public)
│   ├── Services/
│   │   ├── I*.cs                    — public interfaces
│   │   ├── *.cs                     — internal implementations
│   │   └── ServiceCollectionExtensions.cs — AddImageClassificationCore()
│   └── Helpers/
│
├── ImageClassification.UI/
│   ├── AppServiceFactory.cs         — DI composition root (calls AddXxx() only)
│   ├── ViewModels/                  — MVVM ViewModels
│   ├── Views/                       — XAML views
│   └── Converters/                  — value converters
│
└── ImageClassification.Tests/
    ├── Models/                      — model unit tests
    └── Services/                    — service unit + integration tests
```

## DI Registration

All registrations are encapsulated in library-level extension methods. The UI composition root only calls these:

```csharp
services.AddArchiveReader();        // from ArchiveReader
services.AddVectorStore();          // from VectorStore
services.AddImageClassificationCore(); // from Core

// ViewModels and Window (UI-specific, not in library)
services.AddSingleton<MainViewModel>();
services.AddTransient<MainWindow>();
```

See [`AGENTS.md`](AGENTS.md) for full DIP conventions.

## Training Pipeline

- **Optimizer**: AdamW (lr=1e-3, weight_decay=0.01)
- **Augmentation**: TrivialAugment + MixUp (alpha=0.2) + CutMix (alpha=1.0)
- **Regularization**: Label Smoothing (epsilon=0.1)
- **LR Schedule**: Cosine Annealing + 5-epoch warmup
- **Transfer Learning**: Progressive unfreezing

## License

MIT
