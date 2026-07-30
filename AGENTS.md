# AGENTS.md — Architecture & Conventions

## Dependency Inversion Principle (DIP)

All cross-project dependencies MUST go through interfaces, never concrete types.

### Rules

1. **Interfaces** are `public` and define the contract.
2. **Implementations** are `internal sealed class` — never `public`.
3. **Registration** is done inside the owning library via `ServiceCollectionExtensions`.
4. **Callers** resolve only interfaces through DI — never `new` a concrete type, never reference a concrete type in registration.

### Interface placement

| Location | Namespace | Contains |
|---|---|---|
| `Core/Interfaces/` | `ImageClassification.Core.Interfaces` | Cross-project interfaces (`IArchiveReader`, `IVectorStore`) |
| `Core/Services/` | `ImageClassification.Core.Services` | Service interfaces (`IClipImageEncoder`, `IModelDownloader`, etc.) |

- Cross-project interfaces are centralized in `Core` even when implementations live in separate libraries (`ArchiveReader`, `VectorStore`).
- All implementations are `internal sealed class` in their owning project.

### InternalsVisibleTo

Granted from each library `.csproj`:

```xml
<ItemGroup>
    <InternalsVisibleTo Include="ImageClassification.Tests" />
    <InternalsVisibleTo Include="DynamicProxyGenAssembly2" PublicKey="0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7" />
</ItemGroup>
```

`DynamicProxyGenAssembly2` is required by Moq for mocking generic types over internal classes (e.g. `ILogger<InternalClass>`). The public key is mandatory because the assembly is strong-named.

---

## ServiceCollectionExtensions Conventions

| Rule | Value |
|---|---|
| Method prefix | `Add` — never `Use` (reserved for middleware pipeline) |
| Namespace | `Microsoft.Extensions.DependencyInjection` (auto‑discoverable) |
| Return type | `IServiceCollection` (for chaining) |
| File placement | One file per library: `ServiceCollectionExtensions.cs` |
| Class name | `{Feature}ServiceCollectionExtensions` |
| Lifetime | All services are `Singleton` — no Transient or Scoped |

### Decorator pattern

For services that need caching, register the raw concrete first, then map the interface to the decorator:

```csharp
services.AddSingleton<ClipImageEncoder>();                          // raw concrete
services.AddSingleton<IClipImageEncoder, CachedClipImageEncoder>();  // decorator wrapping the concrete
```

For the text encoder, a factory is required to resolve the concrete from DI:

```csharp
services.AddSingleton<ClipTextEncoder>();
services.AddSingleton<IClipTextEncoder>(sp => sp.GetRequiredService<ClipTextEncoder>());
```

### Factory for `IModelDownloader`

Use an explicit factory instead of `AddSingleton<IModelDownloader, ModelDownloader>()` to prevent the DI container from resolving `IEnumerable<ModelDownloadInfo>` as empty and bypassing the default model list:

```csharp
services.AddSingleton<IModelDownloader>(_ => new ModelDownloader());
```

### Extension signatures

```csharp
namespace Microsoft.Extensions.DependencyInjection;

public static class ArchiveReaderServiceCollectionExtensions
{
    public static IServiceCollection AddArchiveReader(this IServiceCollection services)
    {
        services.AddSingleton<IArchiveReader, ArchiveReader>();
        return services;
    }
}
```

### Current extensions

| Project | Method | Registers |
|---|---|---|
| `ImageClassification.ArchiveReader` | `AddArchiveReader()` | `IArchiveReader` → `ArchiveReader` |
| `ImageClassification.VectorStore` | `AddVectorStore()` | `IVectorStore` → `SqliteVectorStore` |
| `ImageClassification.Core` | `AddImageClassificationCore()` | All 12 service interfaces + decorators + factories (15 entries total) |

---

## Exceptions (public types)

- **Models/DTOs** (`ModelDownloadInfo`, `ClassificationResult`, etc.) — cross assembly boundaries as data contracts.
- **ServiceCollectionExtensions classes** — the DI entry point, must be `public static`.
- **Interfaces** — the contract between projects.