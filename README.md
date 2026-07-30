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
- **Auto-Cluster**: k-means clustering without predefined labels
  - Auto-determine cluster count (silhouette score)
  - Editable cluster names
  - Sort files into clusters
- **By Tags**: CLIP zero-shot tag generation
  - Multi-line candidate tag input
  - Image preview with tag checkboxes
  - Edit and filter tags before sorting

### Comic Cover Search (Tab 6)
- Search CBZ/CBR comic archives by semantic description
- Uses CLIP ViT-B/32 ONNX model for text + image embeddings
- Results ranked by cosine similarity (raw CLIP score)
- Adjustable similarity threshold (default 0.25)
- Score distribution shown in status bar

### Settings (Tab 5)
- ONNX Runtime thread count
- Light/Dark theme
- GPU acceleration (CUDA)
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
