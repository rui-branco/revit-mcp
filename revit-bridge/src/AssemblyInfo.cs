using System.Runtime.CompilerServices;

// The handlers assembly is the other half of this one, not a third party: it implements
// IBridgeRouter, throws BridgeException, reads bodies with JsonBody and writes through BridgeJson.
// Making all of that public purely to split the build would have been a worse trade - the bridge
// deliberately exposes no public surface but BridgeApplication, which is the only type Revit needs.
[assembly: InternalsVisibleTo("RevitMcpBridge.Handlers")]
