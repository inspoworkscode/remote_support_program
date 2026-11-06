using System.Runtime.CompilerServices;

// The bench drives the raw Media Foundation layer directly, on purpose. Reading a converted NV12
// frame back to the CPU to look at it is exactly what the product must never do, so the code that
// does it belongs in the harness rather than behind a public method on a production class that
// nothing in production would ever call.
[assembly: InternalsVisibleTo("codecbench")]
