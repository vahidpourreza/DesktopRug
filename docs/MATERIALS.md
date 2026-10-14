# Rug materials

The three supplied reference images replace the earlier procedural designs. Their original PNG bytes are retained in `src/DesktopRug.App/Resources/Rugs/` and embedded in the portable executable; no external image folder or download is required at runtime.

| Choice | Embedded source | Body texture | Width / height |
| --- | --- | --- | --- |
| Rich red Persian | `rich-red-persian.png` | 1020 × 1275 | 0.8 |
| Light ivory Persian | `light-ivory-persian.png` | 966 × 1380 | 0.7 |
| Neutral tonal | `neutral-tonal.png` | 1323 × 980 | 1.35 |

`PatternFactory` maps a measured quadrilateral inside each photographed fabric body to its texture. This excludes the surrounding floor, straightens the slight edge variation, and preserves the supplied ornament and palette instead of substituting regenerated motifs. Bilinear sampling avoids hard resampling steps; a subtle optional fibre layer supplements the weave already visible in the references. The renderer adds live lighting, shadows, edge thickness and individual fringe strands; the portrait designs have fringe at top/bottom, while the landscape design has it at left/right. These rendering effects are not claimed to be a pixel-identical photograph cutout.

The old serialized enum identifiers (`Kashan`, `Tabriz`, `Modern`) remain stable so existing settings load safely. Their visible English/Persian labels and materials now map to the three references above. Custom images retain their existing fit-without-cropping behavior and never overwrite their source.

The material diagnostic exports detail-level textures plus flat, folded, rolled and unrolled cloth renders. It checks deterministic pixels, resolution, proportions, opacity, distinct materials, finite geometry, clear outside pixels and roll geometry. These are renderer/simulation checks, not physical desktop-input or hardware acceptance.
