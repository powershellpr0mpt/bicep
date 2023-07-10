// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading.Tasks;
using Azure;
using Azure.Containers.ContainerRegistry;
using Azure.Identity;
using Bicep.Core.Configuration;
using Bicep.Core.Modules;
using Bicep.Core.Registry.Oci;
using OciDescriptor = Bicep.Core.Registry.Oci.OciDescriptor;
using OciManifest = Bicep.Core.Registry.Oci.OciManifest;

namespace Bicep.Core.Registry
{
    public class AzureContainerRegistryManager
    {
        // media types are case-insensitive (they are lowercase by convention only)
        private const StringComparison MediaTypeComparison = StringComparison.OrdinalIgnoreCase;
        private const StringComparison DigestComparison = StringComparison.Ordinal;

        private readonly IContainerRegistryClientFactory clientFactory;

        public AzureContainerRegistryManager(IContainerRegistryClientFactory clientFactory)
        {
            this.clientFactory = clientFactory;
        }

        public async Task<OciArtifactResult> PullArtifactAsync(RootConfiguration configuration, OciArtifactModuleReference moduleReference)
        {
            ContainerRegistryContentClient client;
            OciManifest manifest;
            Stream manifestStream;
            string manifestDigest;

            async Task<(ContainerRegistryContentClient, OciManifest, Stream, string)> DownloadManifestInternalAsync(bool anonymousAccess)
            {
                var client = this.CreateBlobClient(configuration, moduleReference, anonymousAccess);
                var (manifest, manifestStream, manifestDigest) = await DownloadManifestAsync(moduleReference, client);
                return (client, manifest, manifestStream, manifestDigest);
            }

            try
            {
                // Try authenticated client first.
                Trace.WriteLine($"Authenticated attempt to pull artifact for module {moduleReference.FullyQualifiedReference}.");
                (client, manifest, manifestStream, manifestDigest) = await DownloadManifestInternalAsync(anonymousAccess: false);
            }
            catch (RequestFailedException exception) when (exception.Status == 401 || exception.Status == 403)
            {
                // Fall back to anonymous client.
                Trace.WriteLine($"Authenticated attempt to pull artifact for module {moduleReference.FullyQualifiedReference} failed, received code {exception.Status}. Fallback to anonymous pull.");
                (client, manifest, manifestStream, manifestDigest) = await DownloadManifestInternalAsync(anonymousAccess: true);
            }
            catch (CredentialUnavailableException)
            {
                // Fall back to anonymous client.
                Trace.WriteLine($"Authenticated attempt to pull artifact for module {moduleReference.FullyQualifiedReference} failed due to missing login step. Fallback to anonymous pull.");
                (client, manifest, manifestStream, manifestDigest) = await DownloadManifestInternalAsync(anonymousAccess: true);
            }

            var moduleStream = await ProcessManifest(client, manifest);

            return new OciArtifactResult(manifestDigest, manifest, manifestStream, moduleStream);
        }

//asdfg https://learn.microsoft.com/en-us/dotnet/api/overview/azure/containers.containerregistry-readme?view=azure-dotnet#upload-images
//asdfg https://learn.microsoft.com/en-us/azure/container-registry/container-registry-image-formats#oci-artifacts
// asdfg Azure Container Registry supports the OCI Distribution Specification, a vendor-neutral, cloud-agnostic spec to store, share, secure, and deploy container images and other content types (artifacts). The specification allows a registry to store a wide range of artifacts in addition to container images. You use tooling appropriate to the artifact to push and pull artifacts. For examples, see:
//asdfg https://github.com/oras-project/artifacts-spec/blob/main/scenarios.md


        public async Task PushArtifactAsync(RootConfiguration configuration, OciArtifactModuleReference moduleReference, string? artifactType, StreamDescriptor config, Stream? bicepSources/*asdfg dono't pass this in*/, string? documentationUri = null, string? description = null, params StreamDescriptor[] layers)
        {
            // TODO: How do we choose this? Does it ever change?
            var algorithmIdentifier = DescriptorFactory.AlgorithmIdentifierSha256;

            // push is not supported anonymously
            var blobClient = this.CreateBlobClient(configuration, moduleReference, anonymousAccess: false);

            config.ResetStream();
            var configDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, config);

            config.ResetStream();
            var result1 = await blobClient.UploadBlobAsync(config.Stream);

            var layerDescriptors = new List<OciDescriptor>(layers.Length);
            foreach (var layer in layers)
            {
                layer.ResetStream();
                var layerDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, layer);
                layerDescriptors.Add(layerDescriptor);

                layer.ResetStream();
                var result2 = await blobClient.UploadBlobAsync(layer.Stream);
            }

            var annotations = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(documentationUri))
            {
                annotations[LanguageConstants.OciOpenContainerImageDocumentationAnnotation] = documentationUri;
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                annotations[LanguageConstants.OciOpenContainerImageDescriptionAnnotation] = description;
            }

            var manifest = new OciManifest(2, null, artifactType, configDescriptor, layerDescriptors, null);

            using var manifestStream = new MemoryStream();
            OciSerialization.Serialize(manifestStream, manifest);

            manifestStream.Position = 0;
            var manifestBinaryData = await BinaryData.FromStreamAsync(manifestStream);
            var manifestUploadResult = await blobClient.SetManifestAsync(manifestBinaryData, moduleReference.Tag, mediaType: ManifestMediaType.OciImageManifest);

            manifestStream.Position = 0;
            var manifestStreamDescriptor = new StreamDescriptor(manifestStream, ManifestMediaType.OciImageManifest.ToString()/*asdfg*/); //asdfg
            var manifestDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, manifestStreamDescriptor);

            if (bicepSources is not null)
            {
                //var config = new StreamDescriptor(Stream.Null, BicepMediaTypes.BicepModuleConfigV1); //asdfg
                var layer = new StreamDescriptor(bicepSources, "application/vnd.oci.image.layer.v1.tar");//, BicepMediaTypes.BicepModuleLayerV1Json);

                //asdfg??????  var configasdfg = new StreamDescriptor(Stream.Null, BicepMediaTypes.BicepModuleSourcesArtifactType/*asdfg?  mediatype of config same as artifact type of manifest?*/, new Dictionary<string, string> { { "asdfg1", "asdfg value" } });
                //var configasdfg = new StreamDescriptor(Stream.Null, "application/vnd.oci.image.manifest.v1+json"/*asdfg?  mediatype of config same as artifact type of manifest?*/);//, new Dictionary<string, string> { { "asdfg1", "asdfg value" } });
                // NOTE: Azure Container Registries won't recognize this as a valid attachment with this being valid JSON, so write out an empty object
                using var innerConfigStream = new MemoryStream(new byte[] { (byte)'{', (byte)'}' });
                var configasdfg = new StreamDescriptor(innerConfigStream, "hello/example"/*asdfg?  mediatype of config same as artifact type of manifest?*/);//, new Dictionary<string, string> { { "asdfg1", "asdfg value" } });
                var configasdfgDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, configasdfg);

                // Upload config blob     asdfg can this be skipped?
                configasdfg.ResetStream();
                var asdfgresponse1 = await blobClient.UploadBlobAsync(configasdfg.Stream); // asdfg should get digest from result



                //var layerasdfg = new StreamDescriptor(bicepSources, "hello/example");// "application/vnd.oci.image.manifest.v1+json"); //asdfg? BicepMediaTypes.BicepModuleSourcesV1Layer/*asdfg?*/);//asdfg, new Dictionary<string, string> { { "asdfg-title", "sourcesasdfg.zip" } });
                //var layerasdfg = new StreamDescriptor(bicepSources, "hello/example");//, new Dictionary<string, string> { { "asdfg-title", "a.txt" } });
                var layerasdfg = new StreamDescriptor(bicepSources, "application/vnd.oci.image.layer.v1.tar", new Dictionary<string, string> { { "org.opencontainers.image.title", "a.txt" } });
                layerasdfg.ResetStream();
                var layerasdfgDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, layerasdfg);

                layerasdfg.ResetStream();
                var asdfgresponse2 = await blobClient.UploadBlobAsync(layerasdfg.Stream);

                var manifestasdfg = new OciManifest(
                    2,
                    "application/vnd.oci.image.manifest.v1+json",
                    null, //"application/vnd.oci.image.manifest.v1+json", //asdfg BicepMediaTypes.BicepModuleSourcesArtifactType/*asdfg?*/,
                    configasdfgDescriptor,
                    new List<OciDescriptor> { layerasdfgDescriptor },
                    subject: manifestDescriptor
                    //asdfg new Dictionary<string, string> { { "asdfg3", "asfg3 value" } });
                    );

                using var manifestasdfgStream = new MemoryStream();
                OciSerialization.Serialize(manifestasdfgStream, manifestasdfg);

                manifestasdfgStream.Position = 0;
                var manifestasdfgBinaryData = await BinaryData.FromStreamAsync(manifestasdfgStream);
                var manifestasdfgUploadResult = await blobClient.SetManifestAsync(manifestasdfgBinaryData, null);//, mediaType: ManifestMediaType.OciImageManifest/*asdfg?*/);
            }
        }

        private static Uri GetRegistryUri(OciArtifactModuleReference moduleReference) => new($"https://{moduleReference.Registry}");

        private ContainerRegistryContentClient CreateBlobClient(RootConfiguration configuration, OciArtifactModuleReference moduleReference, bool anonymousAccess) => anonymousAccess
            ? this.clientFactory.CreateAnonymousBlobClient(configuration, GetRegistryUri(moduleReference), moduleReference.Repository)
            : this.clientFactory.CreateAuthenticatedBlobClient(configuration, GetRegistryUri(moduleReference), moduleReference.Repository);

        private static async Task<(OciManifest, Stream, string)> DownloadManifestAsync(OciArtifactModuleReference moduleReference, ContainerRegistryContentClient client)
        {
            Response<GetManifestResult> manifestResponse;
            try
            {
                // either Tag or Digest is null (enforced by reference parser)
                var tagOrDigest = moduleReference.Tag
                    ?? moduleReference.Digest
                    ?? throw new ArgumentNullException(nameof(moduleReference), $"The specified module reference has both {nameof(moduleReference.Tag)} and {nameof(moduleReference.Digest)} set to null.");

                manifestResponse = await client.GetManifestAsync(tagOrDigest);
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                // manifest does not exist
                throw new OciModuleRegistryException("The module does not exist in the registry.", exception);
            }

            // the Value is disposable, but we are not calling it because we need to pass the stream outside of this scope
            var stream = manifestResponse.Value.Manifest.ToStream();

            // BUG: The SDK internally consumed the stream for validation purposes and left position at the end
            stream.Position = 0;
            ValidateManifestResponse(manifestResponse);

            // the SDK doesn't expose all the manifest properties we need
            // so we need to deserialize the manifest ourselves to get everything
            stream.Position = 0;
            var deserialized = DeserializeManifest(stream);
            stream.Position = 0;

            return (deserialized, stream, manifestResponse.Value.Digest);
        }

        private static void ValidateManifestResponse(Response<GetManifestResult> manifestResponse)
        {
            var digestFromRegistry = manifestResponse.Value.Digest;
            var stream = manifestResponse.Value.Manifest.ToStream();

            // TODO: The registry may use a different digest algorithm - we need to handle that
            string digestFromContent = DescriptorFactory.ComputeDigest(DescriptorFactory.AlgorithmIdentifierSha256, stream);

            if (!string.Equals(digestFromRegistry, digestFromContent, DigestComparison))
            {
                throw new OciModuleRegistryException($"There is a mismatch in the manifest digests. Received content digest = {digestFromContent}, Digest in registry response = {digestFromRegistry}");
            }
        }

        private static async Task<Stream> ProcessManifest(ContainerRegistryContentClient client, OciManifest manifest)
        {
            // Bicep versions before 0.14 used to publish modules without the artifactType field set in the OCI manifest,
            // so we must allow null here
            if (manifest.ArtifactType is not null && !string.Equals(manifest.ArtifactType, BicepMediaTypes.BicepModuleArtifactType, MediaTypeComparison))
            {
                throw new InvalidModuleException($"Expected OCI artifact to have the artifactType field set to either null or '{BicepMediaTypes.BicepModuleArtifactType}' but found '{manifest.ArtifactType}'.", InvalidModuleExceptionKind.WrongArtifactType);
            }

            ProcessConfig(manifest.Config);
            if (manifest.Layers.Length != 1)
            {
                throw new InvalidModuleException("Expected a single layer in the OCI artifact.");
            }

            var layer = manifest.Layers.Single();

            return await ProcessLayer(client, layer);
        }

        private static void ValidateBlobResponse(Response<DownloadRegistryBlobResult> blobResponse, OciDescriptor descriptor)
        {
            var stream = blobResponse.Value.Content.ToStream();

            if (descriptor.Size != stream.Length)
            {
                throw new InvalidModuleException($"Expected blob size of {descriptor.Size} bytes but received {stream.Length} bytes from the registry.");
            }

            stream.Position = 0;
            string digestFromContents = DescriptorFactory.ComputeDigest(DescriptorFactory.AlgorithmIdentifierSha256, stream);
            stream.Position = 0;

            if (!string.Equals(descriptor.Digest, digestFromContents, DigestComparison))
            {
                throw new InvalidModuleException($"There is a mismatch in the layer digests. Received content digest = {digestFromContents}, Requested digest = {descriptor.Digest}");
            }
        }

        private static async Task<Stream> ProcessLayer(ContainerRegistryContentClient client, OciDescriptor layer)
        {
            if (!string.Equals(layer.MediaType, BicepMediaTypes.BicepModuleLayerV1Json, MediaTypeComparison))
            {
                throw new InvalidModuleException($"Did not expect layer media type \"{layer.MediaType}\".", InvalidModuleExceptionKind.WrongModuleLayerMediaType);
            }

            Response<DownloadRegistryBlobResult> blobResult;
            try
            {
                blobResult = await client.DownloadBlobContentAsync(layer.Digest);
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                throw new InvalidModuleException($"Module manifest refers to a non-existent blob with digest \"{layer.Digest}\".", exception);
            }

            ValidateBlobResponse(blobResult, layer);

            return blobResult.Value.Content.ToStream();
        }

        private static void ProcessConfig(OciDescriptor config)
        {
            // media types are case insensitive
            if (!string.Equals(config.MediaType, BicepMediaTypes.BicepModuleConfigV1, MediaTypeComparison))
            {
                throw new InvalidModuleException($"Did not expect config media type \"{config.MediaType}\".");
            }

            if (config.Size != 0)
            {
                throw new InvalidModuleException("Expected an empty config blob.");
            }
        }

        private static OciManifest DeserializeManifest(Stream stream)
        {
            try
            {
                return OciSerialization.Deserialize<OciManifest>(stream);
            }
            catch (Exception exception)
            {
                throw new InvalidModuleException("Unable to deserialize the module manifest.", exception);
            }
        }
    }
}
