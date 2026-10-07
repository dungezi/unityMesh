# Bundled simplifier

Source: https://github.com/Whinarn/UnityMeshSimplifier

Pinned commit: `53fdb3122645bcd3ad2c258235200dff3dfbaa9b`.

Only the mesh simplifier and supporting data/math classes are included. The namespace is isolated as `StylizedMesh.Simplification` to avoid collisions with an existing installation. No network access is needed by the plugin.

Local changes: protected vertices and their immediate neighbors cannot collapse; smart-linked vertices propagate protection; bone weights are barycentrically interpolated, reduced to the strongest four influences and normalized. A zero-width smart-link bound is guarded; simplification supports cancellation. The caller restores the source UV layout rather than inferring it from nonzero components. Meshes with more than four influences per vertex are rejected by simplification rather than silently truncated; vertex edits and flat shading preserve all influences.

Upstream MIT license and third-party notices are included in this directory. Source-file license notices are retained.
