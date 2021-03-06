﻿// Copyright (c) 2016 Microsoft Corporation. All Rights Reserved.
// Licensed under the MIT License (MIT)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;
using Microsoft.OData.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using RSAccessor.PortalAccessor.OData.V2.Model;

namespace RSAccessor.PortalAccessor
{
    public class RSPortalAccessorV2
    {
        internal const string CatalogItemContentType = "application/json;odata.metadata=minimal";

        private readonly string _reportServerPortalUrl;

        public RSPortalAccessorV2(string reportServerPortalUrl)
        {
            _reportServerPortalUrl = reportServerPortalUrl;
        }

        public ICredentials ExecuteCredentials { get; set; }

        public Container CreateContext()
        {
            var container = new Container(new Uri(_reportServerPortalUrl));
            ContextFactory.InitializeContainer(_reportServerPortalUrl, ExecuteCredentials, container);
            return container;
        }

        public static string CreateFullPath(params string[] pathParts)
        {
            var seperator = '/';
            var seperators = new[] { seperator };
            for (int i = 0; i < pathParts.Length; i++)
            {
                pathParts[i] = pathParts[i].TrimStart(seperators).TrimEnd(seperators);
            }

            return (seperator + string.Join(seperator.ToString(), pathParts)).Replace("//", "/");
        }

        public string AddToCatalogItems<T>(
            string itemName,
            string parentFolder,
            byte[] content) where T : CatalogItem, new()
        {
            var item = new T();
            item.Path = CreateFullPath(parentFolder, itemName);
            item.Name = itemName;
            item.Content = content;
            item.Type = ResolveCatalogItemType(item);

            var ctx = CreateContext();
            ctx.AddToCatalogItems(item);
            ProceedIfExists(() => ctx.SaveChanges());

            return item.Path;
        }

        public void AddToCatalogItems(CatalogItem item)
        {
            item.Type = ResolveCatalogItemType(item);
            var ctx = CreateContext();
            ctx.AddToCatalogItems(item);
            ProceedIfExists(() => ctx.SaveChanges());
        }

        public string AddToCatalogItems<T>(
            string itemName,
            string parentFolder,
            string json) where T : CatalogItem, new()
        {
            var path = CreateFullPath(parentFolder, itemName);
            var item = JObject.Parse(json);
            item.Property("Name").Value = itemName;
            item.Property("Path").Value = path;
            var content = item.ToString(Newtonsoft.Json.Formatting.None);

            var credentials = ExecuteCredentials ?? CredentialCache.DefaultNetworkCredentials;
            var context = CreateContext();

            using (var client = new WebClient
            {
                Credentials = credentials,
                BaseAddress = _reportServerPortalUrl,
                Encoding = Encoding.UTF8
            })
            {
                client.Headers.Add(HttpRequestHeader.ContentType, CatalogItemContentType);
                client.Headers.Add(ContextFactory.GetHeaders());
                ProceedIfExists(() => client.UploadString(context.CatalogItems.RequestUri, content));
            }

            return item.Path;
        }

        public void SetDataModelDataSourceCredentials(
            string path,
            string username,
            string password,
            bool isWindowsCredentials)
        {
            if (String.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }

            var ctx = CreateContext();
            var dictionary = new Dictionary<string, object>();
            dictionary.Add("Path", path);
            dictionary.Add("Random", "Value");

            var powerBiReport = ctx.PowerBIReports.ByKey(dictionary).Expand("DataSources").GetValue();
            if (powerBiReport != null)
            {
                if (powerBiReport.DataSources != null)
                {
                    foreach (var dataSource in powerBiReport.DataSources)
                    {
                        dataSource.DataModelDataSource.AuthType = 
                            isWindowsCredentials ? DataModelDataSourceAuthType.Windows : DataModelDataSourceAuthType.UsernamePassword;
                        dataSource.DataModelDataSource.Username = username;
                        dataSource.DataModelDataSource.Secret = password;
                    }

                    UpdatePbiReportDataSources(powerBiReport.DataSources, ctx.BaseUri, powerBiReport.Id, ExecuteCredentials);
                }
            }
        }

        public void SetDataModelDataSourceCredentials(
            string path,
            List<DataSource> userDataSources)
        {
            if (String.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }

            var ctx = CreateContext();
            var dictionary = new Dictionary<string, object>();
            dictionary.Add("Path", path);
            dictionary.Add("Random", "Value");

            var powerBiReport = ctx.PowerBIReports.ByKey(dictionary).Expand("DataSources").GetValue();
            if (powerBiReport != null)
            {
                if (powerBiReport.DataSources != null)
                {
                    foreach (var dataSource in powerBiReport.DataSources)
                    {
                        var userDataSource = userDataSources.Where(ds =>
                            String.Equals(ds.ConnectionString, dataSource.ConnectionString, StringComparison.OrdinalIgnoreCase) &&
                            ds.DataModelDataSource != null &&
                            dataSource.DataModelDataSource != null &&
                            ds.DataModelDataSource.Kind == dataSource.DataModelDataSource.Kind).FirstOrDefault();

                        dataSource.DataModelDataSource.AuthType = userDataSource.DataModelDataSource.AuthType;
                        dataSource.DataModelDataSource.Username = userDataSource.DataModelDataSource.Username;
                        dataSource.DataModelDataSource.Secret = userDataSource.DataModelDataSource.Secret;
                    }

                    UpdatePbiReportDataSources(powerBiReport.DataSources, ctx.BaseUri, powerBiReport.Id, ExecuteCredentials);
                }

                return;
            }

            throw new ArgumentException(String.Format("No Power BI Report found at path: {0}", path));
        }

        private void UpdatePbiReportDataSources(DataServiceCollection<DataSource> dataSources, Uri serverUri, Guid reportId, ICredentials credentials)
        {
            var updateDataSourceUri = new Uri(serverUri.AbsoluteUri + $"/PowerBIReports({reportId})/DataSources");

            var handler = new HttpClientHandler()
            {
                Credentials = credentials,
                CookieContainer = ContextFactory.CookieContainer
            };

            var client = new HttpClient(handler);

            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new StringEnumConverter());
            var payload = JsonConvert.SerializeObject(dataSources, settings);

            var request = new HttpRequestMessage()
            {
                Method = new HttpMethod("PATCH"),
                RequestUri = updateDataSourceUri,
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            request.Headers.Add(ContextFactory.XsrfToken, ContextFactory.GetXsrfToken(updateDataSourceUri));

            var result = client.SendAsync(request).Result;
            if (result.StatusCode != HttpStatusCode.OK && result.StatusCode != HttpStatusCode.NoContent)
            {
                throw new HttpException((int)result.StatusCode, result.ReasonPhrase);
            }
        }

        private static bool IsConflict(Exception ex)
        {
            if (ex.InnerException is DataServiceClientException && (ex.InnerException as DataServiceClientException).StatusCode == 409)
                return true;
            if (ex is WebException && ((ex as WebException).Response as HttpWebResponse).StatusCode == HttpStatusCode.Conflict)
                return true;

            return false;
        }

        private static void ProceedIfExists(Action addAction)
        {
            try
            {
                addAction?.Invoke();
            }
            catch (Exception ex)
            {
                if (!IsConflict(ex))
                    throw;
            }
        }

        private static CatalogItemType ResolveCatalogItemType(CatalogItem catalogItem)
        {
            if (catalogItem is Report)
                return CatalogItemType.Report;
            if (catalogItem is MobileReport)
                return CatalogItemType.MobileReport;
            if (catalogItem is Kpi)
                return CatalogItemType.Kpi;
            if (catalogItem is Folder)
                return CatalogItemType.Folder;
            if (catalogItem is PowerBIReport)
                return CatalogItemType.PowerBIReport;
            if (catalogItem is DataSource)
                return CatalogItemType.DataSource;
            if (catalogItem is PowerBIReport)
                return CatalogItemType.PowerBIReport;

            return CatalogItemType.Unknown;
        }
    }
}