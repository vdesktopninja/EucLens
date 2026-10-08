# Proposed review

Title: Build EUC Lens portable WPF demo MVP

EUC Lens now provides a five-page Windows desktop workspace for exploring
synthetic remote-session performance. It supports protocol/scenario/workload
selection, one-second charts, contextual diagnostics, local recording and
JSON/CSV exports. Vendor adapters explicitly report Unavailable; persistent
DEMO DATA labeling prevents simulated metrics from being mistaken for real ones.

The solution separates Core, Providers, Diagnostics, App and Tests. A portable
profile produces a self-contained Windows x64 EXE; Windows CI builds/tests and
smoke-tests the published application before uploading artifacts. MIT licensing,
build instructions, architecture, research gates and acceptance checks are included.

Validation: full solution cross-build with zero warnings/errors; 44 xUnit tests
pass; single EXE cross-publish succeeds. Actual Windows execution and visual VDI
acceptance remain pending. See VALIDATION.md. No changes have been pushed.
