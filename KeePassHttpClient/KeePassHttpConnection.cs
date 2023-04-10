﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeePassHttpClient
{
    public class KeePassHttpConnection : IDisposable
    {
        public string Host { get; private set; }
        public int Port { get; private set; }
        public string Hash { get; private set; }
        public string Id { get; private set; }
        public byte[] Key { get; private set; }
        public bool Debug { get; set; } = false;
        public Dictionary<DateTime, KeePassHttpRequest> RequestList { get; set; } = new Dictionary<DateTime, KeePassHttpRequest>();
        public Dictionary<DateTime, KeePassHttpResponse> ResponseList { get; set; } = new Dictionary<DateTime, KeePassHttpResponse>();
        private readonly Aes AesManaged;

        public KeePassHttpConnection(string host, int port, string id, byte[] key)
        {
            this.Host = host;
            this.Port = port;
            this.Hash = null;
            this.Id = id;
            this.Key = key;
            this.AesManaged = key != null ? new AesManaged {Key = key} : new AesManaged();
        }

        public KeePassHttpConnection(string host = "localhost", int port = 19455) : this(host, port, null, null) {}

        public static KeePassHttpConnection FromConnectionInfo(KeePassHttpConnectionInfo connectionInfo)
        {
            return new KeePassHttpConnection(connectionInfo.Host, connectionInfo.Port, connectionInfo.Id, connectionInfo.Key);
        }

        public KeePassHttpConnectionInfo GetConnectionInfo()
        {
            return new KeePassHttpConnectionInfo(this.Host, this.Port, this.Id, this.Key);
        }

        private Uri GetKeePassHttpUri()
        {
            return new Uri(String.Format("http://{0}:{1}", this.Host, this.Port));
        }
        
        private KeePassHttpResponse Send(KeePassHttpRequest request)
        {
            if (this.Debug)
                this.RequestList.Add(DateTime.Now, request);

            try
            {
                using (WebClient webClient = new WebClient())
                {
                    webClient.Headers[HttpRequestHeader.ContentType] = "application/json";
                    string requestString = JsonSerializer.Serialize(request);
                    string responseString = webClient.UploadString(this.GetKeePassHttpUri(), requestString);
                    KeePassHttpResponse response = JsonSerializer.Deserialize<KeePassHttpResponse>(responseString);

                    if (this.Debug)
                        this.ResponseList.Add(DateTime.Now, response);

                    return response;
                }
            }
            catch (WebException ex)
            {
                throw new KeePassHttpException("Error connecting to KeePassHttp", ex);
            }
        }

        public bool Connect()
        {
            if (this.Hash == null)
            {
                KeePassHttpRequest request = new KeePassHttpRequest { RequestType = KeePassHttpRequestType.TEST_ASSOCIATE };
                this.SetVerifier(request);
                KeePassHttpResponse response = this.Send(request);
                this.Hash = response.Hash;
                return response.Success;
            }
            return true;
        }

        public void Disconnect()
        {
            if (AesManaged != null)
                AesManaged.Dispose();

            this.Hash = null;
        }

        private void SetVerifier(KeePassHttpRequest request)
        {
            this.AesManaged.GenerateIV();

            request.Id = this.Id;
            request.Nonce = Convert.ToBase64String(AesManaged.IV);

            using (ICryptoTransform encryptor = this.AesManaged.CreateEncryptor())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(request.Nonce);
                byte[] buffer = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
                request.Verifier = Convert.ToBase64String(buffer);
            }
        }

        private void GenerateKey()
        {
            Random rand = new Random();
            byte[] key = new byte[32];
            rand.NextBytes(key);
            this.Key = key;
            this.AesManaged.Key = key;
        }

        public void Associate()
        {
            if (this.Hash != null)
            {
                KeePassHttpRequest request = new KeePassHttpRequest();
                if (this.Key == null)
                    this.GenerateKey();

                request.Key = Convert.ToBase64String(this.Key);
                this.SetVerifier(request);
                request.RequestType = KeePassHttpRequestType.ASSOCIATE;
                KeePassHttpResponse response = this.Send(request);

                this.Id = response.Id;
                if (!response.Success || this.Id == null)
                {
                    throw new Exception(string.Format("KeePassHttp association failed ({0})", response.Error));
                }
            }
            else
                throw new KeePassHttpException("KeePassHttp disconnected");
        }

        private KeePassCredential DecryptEntry(KeePassHttpResponseEntry entry, string IV)
        {
            string username = String.Empty;
            string password = String.Empty;
            string uuid = String.Empty;
            string name = String.Empty;

            using (ICryptoTransform decryptor = this.AesManaged.CreateDecryptor(this.Key, Convert.FromBase64String(IV)))
            {
                if (!String.IsNullOrEmpty(entry.Password))
                {
                    byte[] bytes = Convert.FromBase64String(entry.Password);
                    password = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(bytes, 0, bytes.Length));
                }

                if (!String.IsNullOrEmpty(entry.Login))
                {
                    byte[] bytes = Convert.FromBase64String(entry.Login);
                    username = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(bytes, 0, bytes.Length));
                }

                if (!String.IsNullOrEmpty(entry.Uuid))
                {
                    byte[] bytes = Convert.FromBase64String(entry.Uuid);
                    uuid = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(bytes, 0, bytes.Length));
                }

                if (!String.IsNullOrEmpty(entry.Name))
                {
                    byte[] bytes = Convert.FromBase64String(entry.Name);
                    name = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(bytes, 0, bytes.Length));
                }

                KeePassCredential credential = new KeePassCredential(username, password, uuid, name);

                if (entry.StringFields != null)
                {
                    foreach (KeePassHttpResponseStringField item in entry.StringFields)
                    {
                        string key = String.Empty;
                        string value = String.Empty;

                        if (!String.IsNullOrEmpty(item.Key))
                        {
                            byte[] bytes = Convert.FromBase64String(item.Key);
                            key = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(bytes, 0, bytes.Length));
                        }

                        if (!String.IsNullOrEmpty(item.Value))
                        {
                            byte[] bytes = Convert.FromBase64String(item.Value);
                            value = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(bytes, 0, bytes.Length));
                        }

                        credential.StringFields.Add(new KeePassHttpResponseStringField(key, value));
                    }
                }

                return credential;
            }
        }

        public KeePassCredential[] RetrieveCredentialsByUrl(string url)
        {
            return this.RetrieveCredentials(url, KeePassHttpRequestSearchField.Url);
        }

        public KeePassCredential[] RetrieveCredentialsByCustomSearchString(string searchString)
        {
            return this.RetrieveCredentials(searchString, KeePassHttpRequestSearchField.Any);
        }

        private KeePassCredential[] RetrieveCredentials(string searchString, KeePassHttpRequestSearchField searchField)
        {
            if (this.Key != null && this.Id != null)
            {
                KeePassHttpRequest request = new KeePassHttpRequest();
                if (searchField == KeePassHttpRequestSearchField.Url)
                    request.RequestType = KeePassHttpRequestType.GET_LOGINS;
                else if (searchField == KeePassHttpRequestSearchField.Any)
                    request.RequestType = KeePassHttpRequestType.GET_LOGINS_CUSTOM_SEARCH;

                this.SetVerifier(request);
                using (ICryptoTransform encryptor = this.AesManaged.CreateEncryptor())
                {

                    byte[] bytes = Encoding.UTF8.GetBytes(searchString);
                    byte[] buffer = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);

                    if (searchField == KeePassHttpRequestSearchField.Url)
                        request.Url = Convert.ToBase64String(buffer);
                    else if (searchField == KeePassHttpRequestSearchField.Any)
                        request.SearchString = Convert.ToBase64String(buffer);
                }

                KeePassHttpResponse response = this.Send(request);

                if (!response.Success)
                    throw new KeePassHttpException(String.Format("Error requesting KeePass credentials ({0})", response.Error ?? "unknown error from KeePassHttp"));

                return response.Entries.Select(entry => this.DecryptEntry(entry, response.Nonce)).ToArray();
            }

            return null;
        }

        public void Dispose()
        {
            this.Disconnect();
        }
    }
}
