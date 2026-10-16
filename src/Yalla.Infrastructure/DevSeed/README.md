# Development seed pictures

`cover.jpg`, `gallery-1.jpg` and `gallery-2.jpg` are the demo branch's cover and gallery, written by
`DevListingSeeder` on a Development start with `DevSeed:Enabled` when the branch has no cover. They
are embedded in `Yalla.Infrastructure` and go through `IPhotoStorage` like an upload, so all three
variants exist and every URL answers.

**They are drawn by code, not photographed.** `generate-images.cs` paints every pixel from rectangles,
circles and curves with SkiaSharp - no photograph, no font, no stock or third-party artwork - so they
carry no licence other than this repository's.

- `cover.jpg` - the room from above: the window wall, the wooden floor with tables 1-4, a planter,
  and the terrace with tables 5-8. It is also the Windows room's photo, and tables 1-4 are pinned
  where they are drawn.
- `gallery-1.jpg` - breakfast from above: a cheese bread on a plate, a coffee and a bowl of greens.
- `gallery-2.jpg` - the terrace at dusk: string lights, rooftops and three tables. It is also the
  Terrace room's photo: tables 5-7 are pinned on the three tables, and 8 between them.

To draw them again, copy `generate-images.cs` to a folder outside the repository (its `global.json`
pins .NET 9, and file-based apps need a .NET 10 SDK) and run:

```
dotnet run generate-images.cs -- <path to this folder>
```

The script is excluded from compilation in `Yalla.Infrastructure.csproj`.
