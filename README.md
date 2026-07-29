# Image Classification WPF

WPF MVVM приложение для классификации и сортировки изображений с использованием современных архитектур CNN.

## Архитектура

Приложение построено по принципу **Dependency Inversion (DIP)** с полной инверсией зависимостей через `Microsoft.Extensions.DependencyInjection`.

```
ImageClassification.Core          — бизнес-логика, модели, сервисы
ImageClassification.UI            — WPF интерфейс (MVVM + CommunityToolkit)
ImageClassification.Tests         — unit-тесты (xUnit + Moq)
```

### Поддерживаемые модели

| Модель | Top-1 ImageNet | Параметры | Размер | Рекомендация |
|--------|---------------|-----------|--------|--------------|
| **EfficientNet-B0** | 78.57% | 5.3M | 21 MB | Лучшая точность/размер |
| **EfficientNet-B1** | 80.40% | 7.8M | 31 MB | По умолчанию |
| **EfficientNetV2-S** | 83.90% | 21.5M | 86 MB | Максимальная точность |
| **ConvNeXt-Tiny** | 82.07% | 28.6M | 114 MB | Современная CNN |
| **MobileNetV3-Large** | 75.51% | 5.5M | 22 MB | Самая быстрая |

## Скачивание моделей

### Встроенный загрузчик (рекомендуется)

Откройте вкладку **Settings → Download Models** и нажмите "Download" напротив нужной модели.

### Ручное скачивание

| Модель | Источник | Ссылка |
|--------|----------|--------|
| EfficientNet-B0 | ONNX Model Zoo | https://github.com/onnx/models/tree/main/validated/vision/classification/efficientnet-lite4 |
| MobileNetV2 | ONNX Model Zoo | https://github.com/onnx/models/tree/main/validated/vision/classification/mobilenet |
| CLIP ViT-B/32 | Hugging Face | https://huggingface.co/openai/clip-vit-base-patch32 |

### Экспорт из PyTorch (для всех архитектур)

```python
# Установка: pip install torch torchvision onnx
import torch
import torchvision.models as models

# Выберите модель:
model = models.efficientnet_b0(pretrained=True)      # EfficientNet-B0
# model = models.efficientnet_b1(pretrained=True)     # EfficientNet-B1
# model = models.efficientnet_v2_s(pretrained=True)   # EfficientNetV2-S
# model = models.convnext_tiny(pretrained=True)        # ConvNeXt-Tiny
# model = models.mobilenet_v3_large(pretrained=True)   # MobileNetV3-Large

model.eval()

# Экспорт в ONNX
dummy = torch.randn(1, 3, 224, 224)
torch.onnx.export(model, dummy, "model.onnx",
                  input_names=["input"],
                  output_names=["output"],
                  dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}},
                  opset_version=17)

print("Model exported to model.onnx")
```

### CLIP для генерации тегов

```python
# Установка: pip install transformers onnx
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

### Куда сохранять модели

Приложение автоматически использует папку: `%APPDATA%/ImageClassification/models/`

Также можно указать путь к модели вручную на вкладках Training, Evaluation, Classify & Sort, Analyze.

## Тренировочный пайплайн (исследовано и оптимизировано)

- **Оптимизатор**: AdamW (lr=1e-3, weight_decay=0.01)
- **Аугментация**: TrivialAugment + MixUp (alpha=0.2) + CutMix (alpha=1.0)
- **Регуляризация**: Label Smoothing (epsilon=0.1)
- **LR Schedule**: Cosine Annealing + 5-epoch warmup
- **Transfer Learning**: Progressive unfreezing (head → последние блоки → полный fine-tune)

### Стек технологий

| Компонент | Библиотека |
|-----------|-----------|
| UI Framework | WPF (.NET 8) |
| MVVM | CommunityToolkit.Mvvm 8.4 |
| ML — обучение | TorchSharp (PyTorch в C#) |
| ML — inference | Microsoft.ML.OnnxRuntime |
| Изображения | SixLabors.ImageSharp |
| Диаграммы | LiveChartsCore.SkiaSharpView |
| DI | Microsoft.Extensions.DependencyInjection |
| Тесты | xUnit + Moq |

## Сборка

```bash
dotnet restore --source https://api.nuget.org/v3/index.json
dotnet build
```

## Структура проекта

```
src/
├── ImageClassification.Core/
│   ├── Models/              — доменные модели и DTO
│   ├── Services/            — интерфейсы + реализации (DIP)
│   │   ├── IImageClassifier → OnnxClassifier
│   │   ├── IModelTrainer    → TorchSharpTrainer
│   │   ├── IModelEvaluator → ModelEvaluator
│   │   ├── IImageSorter     → ImageSorter
│   │   ├── IImageFeatureExtractor → ImageFeatureExtractor
│   │   ├── IClusterService  → ClusterService
│   │   ├── ITagService      → ClipTagService
│   │   └── IModelDownloader → ModelDownloader
│   └── Helpers/             — утилиты для работы с изображениями
│
├── ImageClassification.UI/
│   ├── ViewModels/          — MVVM ViewModels (constructor injection)
│   ├── Views/               — XAML представления (5 вкладок)
│   ├── Converters/          — value converters
│   └── AppServiceFactory.cs — DI-контейнер
│
└── ImageClassification.Tests/
```

## DI-контейнер

Все зависимости регистрируются через интерфейсы в `AppServiceFactory.cs`:

```csharp
services.AddSingleton<IImageClassifier, OnnxClassifier>();
services.AddSingleton<IModelTrainer, TorchSharpTrainer>();
services.AddSingleton<IModelEvaluator, ModelEvaluator>();
services.AddSingleton<IImageSorter, ImageSorter>();
services.AddSingleton<IImageFeatureExtractor, ImageFeatureExtractor>();
services.AddSingleton<IClusterService, ClusterService>();
services.AddSingleton<ITagService, ClipTagService>();
services.AddSingleton<IModelDownloader, ModelDownloader>();
```

## Функциональность

### Training (вкладка 1)
- Выбор архитектуры из 5 моделей
- Настройка гиперпараметров: batch size, epochs, learning rate, weight decay
- Аугментация: TrivialAugment, MixUp, CutMix, Label Smoothing
- Transfer Learning: progressive unfreezing, ImageNet-21k веса
- Прогресс-бар и таблица метрик по эпохам

### Evaluation (вкладка 2)
- Загрузка .onnx модели
- Оценка на тестовом наборе с подпапками классов
- Точность, Precision, Recall, F1 по каждому классу

### Classify & Sort (вкладка 3)
- Классификация изображений с порогом уверенности
- Автоматическая сортировка в подпапки по классам
- Прогресс и лог обработки

### Analyze (вкладка 4)
- **Auto-Cluster**: k-means кластеризация без предопределённых меток
  - Автоопределение количества кластеров (silhouette score)
  - Редактируемые имена кластеров
  - Сортировка по кластерам
- **By Tags**: CLIP zero-shot генерация тегов
  - Multi-line TextBox для кандидатных тегов
  - Превью изображений с тегами и чекбоксами
  - Редактирование и фильтрация тегов перед сортировкой

### Settings (вкладка 5)
- Количество потоков ONNX Runtime
- Выбор темы (Light/Dark)
- GPU ускорение (CUDA)
- **Загрузка моделей**: встроенный загрузчик с прогресс-баром

## Исследование

В папке `research/cnn-models-comparison/` находится полный отчёт с анализом архитектур и сравнением бенчмарков. Ключевые выводы:

- Текущие модели (MobileNetV2, InceptionV3, InceptionResNetV2, NASNetLarge) **устарели** и заменены на EfficientNet/ConvNeXt
- Для <1000 изображений на класс эффективнее средние модели (EfficientNet-B0/B1), чем крупные (NASNetLarge)
- Тренировочный пайплайн с AdamW + TrivialAugment + Cosine Annealing даёт **+2-5%** к точности

## Лицензия

MIT
