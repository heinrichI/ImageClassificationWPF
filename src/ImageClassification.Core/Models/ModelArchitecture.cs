namespace ImageClassification.Core.Models;

public enum ModelArchitecture
{
    EfficientNetB0,
    EfficientNetB1,
    EfficientNetV2S,
    ConvNeXtTiny,
    MobileNetV3Large
}

public static class ModelArchitectureExtensions
{
    public static int GetDefaultImageSize(this ModelArchitecture architecture) => architecture switch
    {
        ModelArchitecture.EfficientNetB0 => 224,
        ModelArchitecture.EfficientNetB1 => 224,
        ModelArchitecture.EfficientNetV2S => 384,
        ModelArchitecture.ConvNeXtTiny => 224,
        ModelArchitecture.MobileNetV3Large => 224,
        _ => 224
    };

    public static string GetDisplayName(this ModelArchitecture architecture) => architecture switch
    {
        ModelArchitecture.EfficientNetB0 => "EfficientNet-B0",
        ModelArchitecture.EfficientNetB1 => "EfficientNet-B1",
        ModelArchitecture.EfficientNetV2S => "EfficientNetV2-S",
        ModelArchitecture.ConvNeXtTiny => "ConvNeXt-Tiny",
        ModelArchitecture.MobileNetV3Large => "MobileNetV3-Large",
        _ => architecture.ToString()
    };

    public static int GetParameterCountMillions(this ModelArchitecture architecture) => architecture switch
    {
        ModelArchitecture.EfficientNetB0 => 5,
        ModelArchitecture.EfficientNetB1 => 8,
        ModelArchitecture.EfficientNetV2S => 21,
        ModelArchitecture.ConvNeXtTiny => 29,
        ModelArchitecture.MobileNetV3Large => 6,
        _ => 0
    };
}
