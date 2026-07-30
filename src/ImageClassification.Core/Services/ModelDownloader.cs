using System.IO;
using System.Net.Http;
using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

internal sealed class ModelDownloader : IModelDownloader
{
    private readonly HttpClient _httpClient;
    private readonly List<ModelDownloadInfo> _models;

    public string ModelsDirectory { get; }

    public ModelDownloader() : this(null)
    {
    }

    public ModelDownloader(IEnumerable<ModelDownloadInfo>? models)
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(30);

        ModelsDirectory = Path.Combine(
            AppContext.BaseDirectory, "models");

        Directory.CreateDirectory(ModelsDirectory);

        _models = models?.ToList() ?? CreateDefaultModelList();
        RefreshDownloadStatus();
    }

    public List<ModelDownloadInfo> GetAvailableModels() => _models;

    public ModelDownloadInfo? GetModel(string filename)
        => _models.FirstOrDefault(m =>
            string.Equals(m.Filename, filename, StringComparison.OrdinalIgnoreCase));

    public string GetModelPath(string filename)
    {
        var model = GetModel(filename)
            ?? throw new InvalidOperationException($"Model '{filename}' not found in available models.");
        return model.LocalPath;
    }

    public string GetLabelsPath()
        => Path.Combine(ModelsDirectory, "imagenet_labels.txt");

    public void RefreshDownloadStatus()
    {
        foreach (var model in _models)
        {
            model.LocalPath = Path.Combine(ModelsDirectory, model.Filename);
        }
    }

    public async Task DownloadModelAsync(ModelDownloadInfo model, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (model.IsDownloaded) return;

        model.LocalPath = Path.Combine(ModelsDirectory, model.Filename);
        var tempPath = model.LocalPath + ".downloading";

        try
        {
            using var response = await _httpClient.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Model download failed for '{model.Name}' ({model.Filename}). " +
                    $"URL: {model.DownloadUrl} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                    $"The model resource may have been moved or removed. " +
                    $"Update the URL in the 'ModelDownload:Models' section of appsettings.json.");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? model.SizeBytes;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    progress?.Report((int)(totalRead * 100 / totalBytes));
                }
            }
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }

        // Move temp file to final location
        if (File.Exists(model.LocalPath))
            File.Delete(model.LocalPath);
        File.Move(tempPath, model.LocalPath);
    }

    public async Task EnsureLabelsFileAsync()
    {
        var labelsPath = GetLabelsPath();
        if (File.Exists(labelsPath)) return;
        await File.WriteAllLinesAsync(labelsPath, ImagenetLabels);
    }

    private static readonly string[] ImagenetLabels =
    {
        "tench", "goldfish", "great white shark", "tiger shark", "hammerhead shark", "electric ray",
        "stingray", "cock", "hen", "ostrich", "brambling", "goldfinch", "house finch", "junco",
        "indigo bunting", "American robin", "bulbul", "jay", "magpie", "chickadee", "American dipper",
        "kite", "bald eagle", "vulture", "great grey owl", "fire salamander", "smooth newt",
        "newt", "spotted salamander", "axolotl", "American bullfrog", "tree frog", "tailed frog",
        "loggerhead sea turtle", "leatherback sea turtle", "mud turtle", "terrapin", "box turtle",
        "banded gecko", "green iguana", "Carolina anole", "desert grassland whiptail lizard",
        "agama", "frilled lizard", "alligator lizard", "Gila monster", "European green lizard",
        "chameleon", "Komodo dragon", "Nile crocodile", "American alligator", "triceratops",
        "worm snake", "ring-necked snake", "eastern hog-nosed snake", "smooth green snake",
        "kingsnake", "garter snake", "water snake", "vine snake", "night snake", "boa constrictor",
        "African rock python", "Indian cobra", "green mamba", "sea snake", "Saharan horned viper",
        "eastern diamondback rattlesnake", "sidewinder", "trilobite", "harvestman", "scorpion",
        "yellow garden spider", "garden spider", "black widow", "tarantula", "wolf spider",
        "tick", "centipede", "black grouse", "ptarmigan", "ruffed grouse", "prairie grouse",
        "peacock", "quail", "partridge", "grey parrot", "macaw", "sulphur-crested cockatoo",
        "lorikeet", "coucal", "bee eater", "hornbill", "hummingbird", "jacamar", "toucan",
        "duck", "red-breasted merganser", "goose", "black swan", "tusker", "echidna",
        "platypus", "wallaby", "koala", "wombat", "jellyfish", "sea anemone", "brain coral",
        "flatworm", "nematode", "conch", "snail", "slug", "sea slug", "chiton", "chambered nautilus",
        "Dungeness crab", "rock crab", "fiddler crab", "red king crab", "American lobster",
        "spiny lobster", "crayfish", "hermit crab", "isopod", "white stork", "black stork",
        "spoonbill", "flamingo", "little blue heron", "great egret", "bittern", "crane",
        "limpkin", "common gallinule", "American coot", "bustard", "ruddy turnstone",
        "dunlin", "common redshank", "dowitcher", "oystercatcher", "pelican", "king penguin",
        "albatross", "grey whale", "killer whale", "dugong", "sea lion", "Chihuahua",
        "Japanese Chin", "Maltese", "Pekingese", "Shih Tzu", "King Charles Spaniel", "Papillon",
        "toy terrier", "Rhodesian Ridgeback", "Afghan Hound", "Basset Hound", "Beagle",
        "Bloodhound", "Bluetick Coonhound", "Black and Tan Coonhound", "Treeing Walker Coonhound",
        "English foxhound", "Redbone Coonhound", "borzoi", "Irish Wolfhound", "Italian Greyhound",
        "Whippet", "Ibizan Hound", "Norwegian Elkhound", "Otterhound", "Saluki", "Scottish Deerhound",
        "Weimaraner", "Staffordshire Bull Terrier", "American Staffordshire Terrier",
        "Bedlington Terrier", "Border Terrier", "Kerry Blue Terrier", "Irish Terrier",
        "Norfolk Terrier", "Norwich Terrier", "Yorkshire Terrier", "Wire Fox Terrier",
        "Lakeland Terrier", "Sealyham Terrier", "Airedale Terrier", "Cairn Terrier",
        "Australian Terrier", "Dandie Dinmont Terrier", "Boston Terrier", "Miniature Schnauzer",
        "Giant Schnauzer", "Standard Schnauzer", "Scottish Terrier", "Tibetan Terrier",
        "Australian Silky Terrier", "Soft-coated Wheaten Terrier", "West Highland White Terrier",
        "Lhasa Apso", "Flat-Coated Retriever", "Curly-Coated Retriever", "Golden Retriever",
        "Labrador Retriever", "Chesapeake Bay Retriever", "Nova Scotia Duck Tolling Retriever",
        "Cocker Spaniel", "English Springer Spaniel", "Welsh Springer Spaniel", "Cavalier King Charles Spaniel",
        "Clumber Spaniel", "American Water Spaniel", "Irish Water Spaniel", "Sussex Spaniel",
        "Pug", "French Bulldog", "Old English Sheepdog", "Shetland Sheepdog", "Collie", "Border Collie",
        "Belgian Tervuren", "Belgian Groenendael", "Belgian Malinois", "Belgian Sheepdog",
        "Briard", "Anatolian Shepherd", "Komondor", "Kuvasz", "Pyrenean Shepherd", "Bernese Mountain Dog",
        "Appenzeller Sennenhund", "Entlebucher Sennenhund", "Boxer", "Bullmastiff", "Tibetan Mastiff",
        "French Bulldog", "Great Dane", "St. Bernard", "Siberian Husky", "Alaskan Malamute",
        "Samoyed", "Pomeranian", "Chow Chow", "Keeshond", "Bichon Frise", "Shiba Inu",
        "Bluetick Coonhound", "Black and Tan Coonhound", "Treeing Walker Coonhound", "English foxhound",
        "Redbone Coonhound", "borzoi", "Irish Wolfhound", "Italian Greyhound", "Whippet",
        "Ibizan Hound", "Norwegian Elkhound", "Otterhound", "Saluki", "Scottish Deerhound",
        "Weimaraner", "Miniature Pinscher", "Toy Manchester Terrier", "Doberman Pinscher",
        "Greater Swiss Mountain Dog", "Bernese Mountain Dog", "Appenzeller Sennenhund",
        "Entlebucher Sennenhund", "Boxer", "Bullmastiff", "Tibetan Mastiff", "Great Dane",
        "St. Bernard", "Siberian Husky", "Alaskan Malamute", "Samoyed", "Pomeranian",
        "Chow Chow", "Keeshond", "Bichon Frise", "Shiba Inu", "Bluetick Coonhound",
        "Black and Tan Coonhound", "Treeing Walker Coonhound", "English foxhound",
        "Redbone Coonhound", "borzoi", "Irish Wolfhound", "Italian Greyhound", "Whippet",
        "Ibizan Hound", "Norwegian Elkhound", "Otterhound", "Saluki", "Scottish Deerhound",
        "Weimaraner", "Miniature Pinscher", "Toy Manchester Terrier", "Doberman Pinscher"
    };

    private List<ModelDownloadInfo> CreateDefaultModelList()
    {
        return new List<ModelDownloadInfo>
        {
            new()
            {
                Name = "EfficientNet-Lite4 (ONNX)",
                Description = "82.78% Top-1, 5.3M params, 384x384. Best accuracy/size ratio.",
                DownloadUrl = "https://huggingface.co/onnxmodelzoo/efficientnet-lite4-11/resolve/main/efficientnet-lite4-11.onnx",
                SizeBytes = 52_000_000,
                Filename = "efficientnet_lite4.onnx"
            },
            new()
            {
                Name = "MobileNetV2 (ONNX)",
                Description = "72.91% Top-1, 3.5M params, 224x224. Lightweight and fast.",
                DownloadUrl = "https://huggingface.co/onnxmodelzoo/mobilenetv2-12/resolve/main/mobilenetv2-12.onnx",
                SizeBytes = 14_000_000,
                Filename = "mobilenet_v2.onnx"
            },
            new()
            {
                Name = "CLIP ViT-B/32",
                Description = "Zero-shot image tagging. ~606 MB (FP32). Required for 'By Tags' mode.",
                DownloadUrl = "https://huggingface.co/openai/clip-vit-base-patch32/resolve/12b36594d53414ecfba93c7200dbb7c7db3c900a/onnx/model.onnx",
                SizeBytes = 606_000_000,
                Filename = "clip_vit_b32.onnx"
            },
            new()
            {
                Name = "CLIP BPE Vocabulary",
                Description = "Byte-pair encoding vocabulary for CLIP text tokenization. ~1.5 MB.",
                DownloadUrl = "https://openaipublic.azureedge.net/clip/bpe_simple_vocab_16e6.txt.gz",
                SizeBytes = 1_500_000,
                Filename = "bpe_simple_vocab_16e6.txt.gz"
            }
        };
    }
}
