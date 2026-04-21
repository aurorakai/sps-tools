// Grants the AuroraKai.SPSTools.Tests assembly access to internal editor types
// (e.g. ConfigStateDetector) so they can be unit-tested directly.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AuroraKai.SPSTools.Tests")]
