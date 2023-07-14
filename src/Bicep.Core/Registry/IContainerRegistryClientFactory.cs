// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Containers.ContainerRegistry;
using Azure.ResourceManager;
using Bicep.Core.Configuration;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Bicep.Core.Registry
{
    /// <summary>
    /// Creates ACR clients.
    /// </summary>
    /// <remarks>This exists because we need to inject mock clients in integration tests and because the real client constructor requires parameters.</remarks>
    public interface IContainerRegistryClientFactory
    {
        ContainerRegistryContentClient CreateAuthenticatedBlobClient(RootConfiguration configuration, Uri registryUri, string repository);
        ContainerRegistryContentClient CreateAnonymousBlobClient(RootConfiguration configuration, Uri registryUri, string repository);

        Task<HttpClient> CreateAuthenticatedHttpClientAsync(RootConfiguration configuration); //asdfg
        //ArmClient CreateAnonymousHttpClientAsync(RootConfiguration configuration);//asdfg
    }
}
