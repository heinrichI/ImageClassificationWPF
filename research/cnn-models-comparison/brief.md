# Research Brief

## Refined Question
Are the current models (MobileNetV2, InceptionV3, InceptionResNetV2, NASNetLarge) and algorithms (transfer learning with ImageNet weights, fine-tuning upper layers, RMSprop optimizer, basic augmentation) optimal for classifying arbitrary photographs with fewer than 1000 images per class, when accuracy is the top priority?

## Scope
- **In**: CNN and ViT architectures for image classification, transfer learning techniques for small datasets, modern training recipes (augmentation, optimizers, schedulers), benchmark comparisons
- **Out**: Object detection, segmentation, NLP, deployment edge cases, hardware-specific optimization
- **Time frame**: 2023-2026 publications and benchmarks
- **Audience**: Developer planning a WPF desktop app reimplementation; needs actionable architecture recommendations

## Assumptions
- Dataset: arbitrary photos, <1000 images per class
- Current approach: Keras/TensorFlow transfer learning from ImageNet
- Deployment target: WPF desktop (CPU inference acceptable, GPU optional)
- Priority: classification accuracy > model size > inference speed

## Depth Mode
standard (3-5 sub-agents, 1 follow-up round, 15+ sources)

## Date
2026-07-07

## Angles

### Angle 1: SOTA CNN Architectures (2024-2026)
What are the current best-performing CNN architectures for image classification? How do EfficientNetV2, ConvNeXtV2, MobileNetV3, and RegNet compare to the older models used in the project?

### Angle 2: Small Dataset Transfer Learning
Which architectures generalize best when fine-tuning on <1000 images per class? What does research say about overfitting risk with large models (NASNetLarge, InceptionResNetV2) on small datasets?

### Angle 3: Modern Training Recipes
What augmentation strategies (RandAugment, TrivialAugment, MixUp, CutMix, label smoothing), optimizers (AdamW, LAMB, SGD+momentum), and learning rate schedules work best for small-dataset transfer learning?

### Angle 4: Vision Transformers vs CNNs for Small Data
Do ViT variants (DeiT-III, SwinV2, ConvNeXt as CNN alternative) outperform CNNs when labeled data is scarce? What about hybrid approaches?

### Angle 5: Practical Trade-offs — Accuracy vs Size vs Speed
What are the actual benchmark numbers (ImageNet, CIFAR-100, Food-101, Stanford Dogs) for the candidate models? How do they compare on accuracy, parameter count, FLOPs, and inference latency on CPU?
