namespace SI.Rosetta.Projections.KurrentDB

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Security
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open System.Text.Json
open SI.Rosetta.Persistence.KurrentDB

type StoredProjectionDefinition = {
    Query: string
    EmitEnabled: bool
}

/// Reads a projection's stored definition over the KurrentDB HTTP API, as the gRPC client can't return it.
/// The address, credentials and TLS options come from the same KurrentDB:ConnectionString.
type KurrentDBProjectionsHttpApi(conf: IConfiguration) =
    let Settings = KurrentDBFactory.CreateSettings conf
    let Connectivity = Settings.ConnectivitySettings
    let RequestTimeout = TimeSpan.FromSeconds(10.0)

    // A single node has an Address, a cluster has gossip seeds: any of its nodes can answer
    let BaseAddresses =
        if not (isNull Connectivity.Address) then [ Connectivity.Address ]
        else
            let scheme = if Connectivity.Insecure then Uri.UriSchemeHttp else Uri.UriSchemeHttps
            Connectivity.GossipSeeds
            |> Array.choose (fun seed ->
                match seed with
                | :? DnsEndPoint as dns -> Some (UriBuilder(scheme, dns.Host, dns.Port).Uri)
                | :? IPEndPoint as ip -> Some (UriBuilder(scheme, ip.Address.ToString(), ip.Port).Uri)
                | _ -> None)
            |> Array.toList

    let IsTrustedByCaFile (certificate: X509Certificate) =
        match certificate with
        | :? X509Certificate2 as serverCertificate ->
            use chain = new X509Chain()
            chain.ChainPolicy.TrustMode <- X509ChainTrustMode.CustomRootTrust
            chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck
            chain.ChainPolicy.CustomTrustStore.Add(Connectivity.TlsCaFile) |> ignore
            chain.Build(serverCertificate)
        | _ -> false

    let CreateClient() =
        let handler = new SocketsHttpHandler()
        if not Connectivity.Insecure then
            if not (isNull Connectivity.ClientCertificate) then
                handler.SslOptions.ClientCertificates <- X509CertificateCollection([| Connectivity.ClientCertificate :> X509Certificate |])
            if not Connectivity.TlsVerifyCert then
                handler.SslOptions.RemoteCertificateValidationCallback <- RemoteCertificateValidationCallback(fun _ _ _ _ -> true)
            elif not (isNull Connectivity.TlsCaFile) then
                // Only the chain may fail the default validation, and only when the tlsCaFile vouches for it
                handler.SslOptions.RemoteCertificateValidationCallback <-
                    RemoteCertificateValidationCallback(fun _ certificate _ errors ->
                        errors = SslPolicyErrors.None
                        || (errors = SslPolicyErrors.RemoteCertificateChainErrors && IsTrustedByCaFile certificate))
        let client = new HttpClient(handler, true, Timeout = RequestTimeout)
        match Settings.DefaultCredentials with
        | null -> ()
        | credentials ->
            let token = Convert.ToBase64String(Encoding.UTF8.GetBytes(sprintf "%s:%s" credentials.Username credentials.Password))
            client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Basic", token)
        client

    /// The stored query and emit setting, or why they couldn't be read
    member this.TryGetDefinitionAsync(name: string) : Task<Result<StoredProjectionDefinition, string>> =
        task {
            use client = CreateClient()
            let mutable result = Error "no KurrentDB address in the connection string"
            let mutable remaining = BaseAddresses
            while not remaining.IsEmpty do
                let address = remaining.Head
                remaining <- remaining.Tail
                let uri = Uri(address, sprintf "projection/%s/query?config=yes" (Uri.EscapeDataString name))
                try
                    use request = new HttpRequestMessage(HttpMethod.Get, uri)
                    request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
                    use! response = client.SendAsync(request).ConfigureAwait(false)
                    if response.IsSuccessStatusCode then
                        let! json = response.Content.ReadAsStringAsync().ConfigureAwait(false)
                        use config = JsonDocument.Parse(json)
                        let mutable query = Unchecked.defaultof<JsonElement>
                        let mutable emitEnabled = Unchecked.defaultof<JsonElement>
                        result <- Ok {
                            Query = if config.RootElement.TryGetProperty("query", &query) then query.GetString() else null
                            // The field is missing when emit is disabled
                            EmitEnabled =
                                config.RootElement.TryGetProperty("emitEnabled", &emitEnabled)
                                && emitEnabled.ValueKind = JsonValueKind.True }
                        remaining <- []
                    else
                        result <- Error (sprintf "GET %s returned %d %O" uri.AbsoluteUri (int response.StatusCode) response.StatusCode)
                with ex ->
                    result <- Error (sprintf "GET %s failed: %s" uri.AbsoluteUri ex.Message)
            return result
        }
