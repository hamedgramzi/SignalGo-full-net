﻿using Newtonsoft.Json.Linq;
using SignalGo.Server.DataTypes;
using SignalGo.Server.Helpers;
using SignalGo.Server.IO;
using SignalGo.Server.Models;
using SignalGo.Shared.DataTypes;
using SignalGo.Shared.Helpers;
using SignalGo.Shared.Http;
using SignalGo.Shared.IO;
using SignalGo.Shared.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SignalGo.Server.ServiceManager.Providers
{
    public class BaseHttpProvider : BaseProvider
    {
        /// <summary>
        /// Guid for web socket client connection
        /// </summary>
        internal static readonly string _guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        internal static async Task HandleHttpRequest(string methodName, string address, ServerBase serverBase, HttpClientInfo client)
        {
            try
            {
                string newLine = TextHelper.NewLine;
                string headerResponse = client.RequestHeaders.ToString();
                if (methodName.ToLower() == "get" && !string.IsNullOrEmpty(address) && address != "/")
                {
                    if (client.RequestHeaders.ContainsKey("content-type") && client.GetRequestHeaderValue("content-type") == "SignalGo Service Reference")
                    {
                        await SendSignalGoServiceReference(client, serverBase);
                    }
                    else
                    {
                        await RunHttpRequest(serverBase, address, "GET", "", client);
                    }
                    serverBase.DisposeClient(client, null, "AddClient finish get call");
                }
                else if (methodName.ToLower() == "post" && !string.IsNullOrEmpty(address) && address != "/")
                {
                    int indexOfStartedContent = headerResponse.IndexOf(TextHelper.NewLine + TextHelper.NewLine);
                    string content = "";
                    if (indexOfStartedContent > 0)
                    {
                        indexOfStartedContent += 4;
                        content = headerResponse.Substring(indexOfStartedContent, headerResponse.Length - indexOfStartedContent);
                    }

                    if (client.RequestHeaders.ContainsKey("signalgo-servicedetail"))
                    {
                        await GenerateServiceDetails(client, content, serverBase, newLine);
                    }
                    else if (client.RequestHeaders["content-type"] != null && client.GetRequestHeaderValue("content-type").ToLower().Contains("multipart/form-data"))
                    {
                        await RunPostHttpRequestFile(address, "POST", content, client, serverBase);
                    }
                    else if (client.RequestHeaders["content-type"] != null && client.GetRequestHeaderValue("content-type") == "SignalGo Service Reference")
                    {
                        await SendSignalGoServiceReference(client, serverBase);
                    }
                    else
                    {
                        await RunHttpRequest(serverBase, address, "POST", content, client);
                    }
                    serverBase.DisposeClient(client, null, "AddClient finish post call");
                }
                else if (methodName.ToLower() == "options" && !string.IsNullOrEmpty(address) && address != "/")
                {
                    if (serverBase.ProviderSetting.HttpSetting.HandleCrossOriginAccess)
                        AddOriginHeader(client, serverBase);
                    string message = newLine + $"Success" + newLine;
                    client.ResponseHeaders.Add("Content-Type", "text/html; charset=utf-8");
                    client.ResponseHeaders.Add("Connection", "Close");

                    byte[] dataBytes = Encoding.UTF8.GetBytes(message);

                    await SendResponseHeadersToClient(HttpStatusCode.OK, client.ResponseHeaders, client, dataBytes.Length);
                    await SendResponseDataToClient(dataBytes, client);
                    serverBase.DisposeClient(client, null, "AddClient finish post call");
                }
                else if (serverBase.RegisteredServiceTypes.ContainsKey("") && (string.IsNullOrEmpty(address) || address == "/"))
                {
                    await RunIndexHttpRequest(client, serverBase);
                    serverBase.DisposeClient(client, null, "Index Page call");
                }
                else
                {
                    client.ResponseHeaders.Add("Content-Type", "text/html");
                    client.ResponseHeaders.Add("Connection", "Close");

                    byte[] dataBytes = Encoding.UTF8.GetBytes(newLine + "SignalGo Server OK" + newLine);
                    await SendResponseHeadersToClient(HttpStatusCode.OK, client.ResponseHeaders, client, dataBytes.Length);
                    await SendResponseDataToClient(dataBytes, client);
                    serverBase.DisposeClient(client, null, "AddClient http ok signalGo");
                }
            }
            catch (Exception ex)
            {
                if (client.IsOwinClient)
                    throw;
                serverBase.DisposeClient(client, null, "HandleHttpRequest exception");
            }
        }

        private static async Task GenerateServiceDetails(HttpClientInfo client, string content, ServerBase serverBase, string newLine)
        {
            string host = "";
            if (client.RequestHeaders.ContainsKey("host"))
                host = client.RequestHeaders["host"].FirstOrDefault();
            ServerServicesManager serverServicesManager = new ServerServicesManager();
            string json = "";
            bool isParameterDetails = client.RequestHeaders["signalgo-servicedetail"].FirstOrDefault() == "parameter";
            if (isParameterDetails)
            {
                int len = int.Parse(client.GetRequestHeaderValue("content-length"));
                if (content.Length < len)
                {
                    List<byte> resultBytes = new List<byte>();
                    int readedCount = 0;
                    while (readedCount < len)
                    {
                        byte[] buffer = new byte[len - content.Length];
                        int readCount = await client.ClientStream.ReadAsync(buffer, buffer.Length);
                        if (readCount <= 0)
                            throw new Exception("zero byte readed socket disconnected!");
                        resultBytes.AddRange(buffer.ToList().GetRange(0, readCount));
                        readedCount += readCount;
                    }
                    json = Encoding.UTF8.GetString(resultBytes.ToArray(), 0, resultBytes.Count);
                }
                MethodParameterDetails detail = ServerSerializationHelper.Deserialize<MethodParameterDetails>(json, serverBase);

                if (!serverBase.RegisteredServiceTypes.TryGetValue(detail.ServiceName, out Type serviceType))
                    throw new Exception($"{client.IPAddress} {client.ClientId} Service {detail.ServiceName} not found");
                if (serviceType == null)
                    throw new Exception($"{client.IPAddress} {client.ClientId} serviceType {detail.ServiceName} not found");

                json = serverServicesManager.SendMethodParameterDetail(serviceType, detail, serverBase);
            }
            else
            {
                ProviderDetailsInfo detail = serverServicesManager.SendServiceDetail(host, serverBase);
                json = ServerSerializationHelper.SerializeObject(detail, serverBase);
            }

            client.ResponseHeaders["Content-Type"] = "application/json; charset=utf-8".Split(',');
            client.ResponseHeaders["Connection"] = "Close".Split(',');
            if (serverBase.ProviderSetting.HttpSetting.HandleCrossOriginAccess)
            {
                AddOriginHeader(client, serverBase);
            }

            byte[] dataBytes = Encoding.UTF8.GetBytes(newLine + json + newLine);
            await SendResponseHeadersToClient(HttpStatusCode.OK, client.ResponseHeaders, client, dataBytes.Length);
            await SendResponseDataToClient(dataBytes, client);
        }


        private static void AddOriginHeader(HttpClientInfo client, ServerBase serverBase)
        {
            if (serverBase.ProviderSetting.HttpSetting.GetCustomOriginFunction == null)
            {
                if (client.RequestHeaders.ContainsKey("origin"))
                {
                    client.ResponseHeaders["Access-Control-Allow-Origin"] = client.RequestHeaders["origin"];
                }
                else
                    client.ResponseHeaders["Access-Control-Allow-Origin"] = new string[] { "*" };
            }
            else
                client.ResponseHeaders["Access-Control-Allow-Origin"] = new string[] { serverBase.ProviderSetting.HttpSetting.GetCustomOriginFunction(client) };

            client.ResponseHeaders["Access-Control-Allow-Credentials"] = new string[] { "true" };
            if (!string.IsNullOrEmpty(client.GetRequestHeaderValue("Access-Control-Request-Headers")))
                client.ResponseHeaders["Access-Control-Allow-Headers"] = client.RequestHeaders["Access-Control-Request-Headers"];
            else
                client.ResponseHeaders["Access-Control-Allow-Headers"] = new string[] { "*" };
            client.ResponseHeaders["Access-Control-Allow-Methods"] = new string[] { "*" };
        }

        /// <summary>
        /// run method of server http class with address and headers
        /// </summary>
        /// <param name="address">address</param>
        /// <param name="headers">headers</param>
        /// <param name="client">client</param>
        internal static async Task RunHttpRequest(ServerBase serverBase, string address, string httpMethod, string content, HttpClientInfo client)
        {

            string newLine = TextHelper.NewLine;

            string fullAddress = address;
            address = address.Trim('/');
            List<string> lines = address.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            //if (lines.Count <= 1)
            //{
            //    string data = newLine + "SignalGo Error: method not found from address: " + address + newLine;
            //    sendInternalErrorMessage(data);
            //    AutoLogger.LogText(data);
            //}
            //else
            //{
            string methodName = lines.Last();
            if (methodName == address)
                address = "";
            string parameters = "";
            string jsonParameters = null;
            Dictionary<string, string> multiPartParameter = new Dictionary<string, string>();
            bool isGet = httpMethod == "GET";
            bool isPost = httpMethod == "POST";
            List<Shared.Models.ParameterInfo> values = new List<Shared.Models.ParameterInfo>();
            if (isGet)
            {
                if (methodName.Contains("?"))
                {
                    string[] sp = methodName.Split(new[] { '?' }, 2);
                    methodName = sp.First();
                    parameters = sp.Last();
                }
            }
            else if (isPost)
            {
                int len = int.Parse(client.GetRequestHeaderValue("content-length"));
                if (content.Length < len)
                {
                    List<byte> resultBytes = new List<byte>();
                    int readedCount = 0;
                    while (readedCount < len)
                    {
                        byte[] buffer = new byte[len - content.Length];
                        int readCount = await client.ClientStream.ReadAsync(buffer, buffer.Length);
                        //#if (NET35 || NET40)
                        //                            int readCount = client.ClientStream.Read(buffer, 0, len - content.Length);
                        //#else
                        //buffer = 
                        //#endif
                        if (readCount <= 0)
                            throw new Exception("zero byte readed socket disconnected!");
                        resultBytes.AddRange(buffer.ToList().GetRange(0, readCount));
                        readedCount += readCount;
                    }
                    byte[] bytes = resultBytes.ToArray();
                    if (serverBase.DecryptRequest != null)
                    {
                        var temp = serverBase.DecryptRequest(client, bytes);
                        if (temp != null || serverBase.ProviderSetting.IsForceEncryption)
                        {
                            bytes = temp;
                        }
                    }
                    string postResponse = Encoding.UTF8.GetString(bytes, 0, bytes.Count());
                    content = postResponse;
                }

                methodName = lines.Last();
                parameters = content;
                jsonParameters = content;
                if (methodName.Contains("?"))
                {
                    string[] sp = methodName.Split(new[] { '?' }, 2);
                    methodName = sp.First();
                    parameters = sp.Last();
                    //if (!string.IsNullOrEmpty(content))
                    //{
                    //    values.Add(new Shared.Models.ParameterInfo() { Value = content });
                    //}
                }
                else if (parameters.StartsWith("----") && parameters.ToLower().Contains("content-disposition"))
                {
                    string boundary = parameters.Split(new string[] { TextHelper.NewLine }, StringSplitOptions.RemoveEmptyEntries)[0];
                    string[] pValues = parameters.Split(new string[] { boundary }, StringSplitOptions.RemoveEmptyEntries);
                    string name = "";
                    foreach (string valueData in pValues)
                    {
                        if (valueData.ToLower().Contains("content-disposition"))
                        {
                            if (valueData.Replace(" ", "").Contains(";name="))
                            {
                                int index = valueData.ToLower().IndexOf("content-disposition");
                                string header = valueData.Substring(index);
                                int headLen = header.IndexOf(TextHelper.NewLine);
                                header = valueData.Substring(index, headLen);
                                string newData = valueData.Substring(index + headLen + 2);
                                //newData = newData.Split(new string[] { boundary }, StringSplitOptions.RemoveEmptyEntries);
                                if (header.ToLower().IndexOf("content-disposition:") == 0)
                                {
                                    CustomContentDisposition disp = new CustomContentDisposition(header);
                                    if (disp.Parameters.ContainsKey("name"))
                                        name = disp.Parameters["name"];
                                    newData = newData.Substring(2, newData.Length - 4);
                                    multiPartParameter.Add(name, newData);
                                }
                            }
                        }
                    }
                }
            }


            methodName = methodName.ToLower();

            lines.RemoveAt(lines.Count - 1);
            address = "";
            foreach (string item in lines)
            {
                address += item + "/";
            }
            address = address.TrimEnd('/').ToLower();
            string callGuid = Guid.NewGuid().ToString();
            string data = null;
            Type serviceType = null;
            MethodInfo method = null;
            try
            {
                //if (!string.IsNullOrEmpty(address) && serverBase.RegisteredServiceTypes.ContainsKey(address))
                //{
                if (multiPartParameter.Count > 0)
                {
                    foreach (KeyValuePair<string, string> item in multiPartParameter)
                    {
                        values.Add(new Shared.Models.ParameterInfo() { Name = item.Key, Value = item.Value });
                    }
                }
                else if (isPost && (client.GetRequestHeaderValue("content-type") == "application/json" || client.GetRequestHeaderValue("accept") == "application/json"))
                {
                    bool hasException = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(content))
                        {
                            JObject des = JObject.Parse(content);
                            foreach (JProperty item in des.Properties())
                            {
                                JToken value = des.GetValue(item.Name);
                                values.Add(new Shared.Models.ParameterInfo() { Name = item.Name, Value = value.ToString() });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        hasException = true;
                        serverBase.AutoLogger.LogError(ex, $"Parse json exception: {parameters} content-Lenth: {client.GetRequestHeaderValue("content-length")} content: {content}");
                    }
                    finally
                    {
                        if (hasException || parameters != content)
                            values.AddRange(GetParametersFromGETMethod(parameters));
                    }
                }
                else
                {
                    parameters = parameters.Trim('&');
                    if (!string.IsNullOrEmpty(parameters))
                    {
                        foreach (string item in parameters.Split(new[] { '&' }))
                        {
                            string[] keyValue = item.Split(new[] { '=' }, 2);
                            values.Add(new Shared.Models.ParameterInfo() { Name = keyValue.Length == 2 ? keyValue[0] : "", Value = Uri.UnescapeDataString(keyValue.Last()) });
                        }
                    }
                    try
                    {
                        if (!string.IsNullOrEmpty(content))
                        {
                            JObject des = JObject.Parse(content);
                            foreach (JProperty item in des.Properties())
                            {
                                JToken value = des.GetValue(item.Name);
                                values.Add(new Shared.Models.ParameterInfo() { Name = item.Name, Value = value.ToString() });
                            }
                        }
                    }
                    catch
                    {

                    }
                }

                CallMethodResultInfo<OperationContext> result = await CallHttpMethod(client, address, methodName, values, jsonParameters, serverBase, method, data, newLine, null, null);
                serviceType = result.ServiceType;

                //}
                //else
                //{
                //    CallMethodResultInfo<OperationContext> result = await CallHttpMethod(client, address, methodName, null, null, serverBase, method, data, newLine, null, null);
                //    serviceType = result.ServiceType;
                //}
            }
            catch (Exception ex)
            {
                // exception = ex;
                if (serverBase.ErrorHandlingFunction != null)
                {
                    ActionResult result = serverBase.ErrorHandlingFunction(ex, serviceType, method, client).ToActionResult();
                    await RunHttpActionResult(client, result.Data, client, serverBase);
                }
                else
                {
                    //data = newLine + ex.ToString() + address + newLine;
                    await SendInternalErrorMessage(ex, address, serviceType, method, serverBase, client, newLine, HttpStatusCode.InternalServerError);
                }
                if (!(ex is SocketException))
                    serverBase.AutoLogger.LogError(ex, "RunHttpRequest");
            }
            finally
            {
                //ClientConnectedCallingCount--;
                //MethodsCallHandler.EndHttpMethodCallAction?.Invoke(client, callGuid, address, method, valueitems, result, exception);
            }
        }

        private static IEnumerable<Shared.Models.ParameterInfo> GetParametersFromGETMethod(string parameters)
        {
            parameters = parameters.Trim('&');
            if (!string.IsNullOrEmpty(parameters))
            {
                foreach (string item in parameters.Split(new[] { '&' }))
                {
                    string[] keyValue = item.Split(new[] { '=' }, 2);
                    yield return new Shared.Models.ParameterInfo() { Name = keyValue.Length == 2 ? keyValue[0] : "", Value = Uri.UnescapeDataString(keyValue.Last()) };
                }
            }
        }


        internal static async Task RunIndexHttpRequest(HttpClientInfo client, ServerBase serverBase)
        {
            string newLine = TextHelper.NewLine;

            MethodInfo method = null;
            Type serviceType = null;

            try
            {
                CallMethodResultInfo<OperationContext> result = await CallHttpMethod(client, "", "-noName-", null, null, serverBase, method, null, newLine, null, x => x.GetCustomAttributes<HomePageAttribute>().Count() > 0);
                serviceType = result.ServiceType;
            }
            catch (Exception ex)
            {
                // exception = ex;
                if (serverBase.ErrorHandlingFunction != null)
                {
                    ActionResult result = serverBase.ErrorHandlingFunction(ex, serviceType, method, client).ToActionResult();
                    await RunHttpActionResult(client, result, client, serverBase);
                }
                else
                {
                    //string data = newLine + ex.ToString() + "" + newLine;
                    await SendInternalErrorMessage(ex, "", serviceType, method, serverBase, client, newLine, HttpStatusCode.InternalServerError);
                }
                if (!(ex is SocketException))
                    serverBase.AutoLogger.LogError(ex, "RunPostHttpRequestFile");
            }
            finally
            {
                //ClientConnectedCallingCount--;
                //MethodsCallHandler.EndHttpMethodCallAction?.Invoke(client, callGuid, "", method, null, result, exception);
            }

        }
        internal static async Task<CallMethodResultInfo<OperationContext>> CallHttpMethod(HttpClientInfo client, string address, string methodName, IEnumerable<Shared.Models.ParameterInfo> values, string jsonParameters, ServerBase serverBase, MethodInfo method
            , string data, string newLine, HttpPostedFileInfo fileInfo, Func<MethodInfo, bool> canTakeMethod)
        {
            if (values != null)
            {
                foreach (Shared.Models.ParameterInfo item in values.Where(x => x.Value == "null"))
                {
                    item.Value = null;
                }
            }
            CallMethodResultInfo<OperationContext> result = await CallMethod(address, _guid, methodName, values?.ToArray(), jsonParameters, client, "", serverBase, fileInfo, canTakeMethod);

            method = result.Method;

            if (result.CallbackInfo.IsException)
            {
                //data = newLine + result.CallbackInfo.Data + newLine;
                await SendInternalErrorMessage(new Exception(result.CallbackInfo.Data), address, result.ServiceType, method, serverBase, client, newLine, (result.CallbackInfo.IsAccessDenied ? serverBase.ProviderSetting.HttpSetting.DefaultAccessDenidHttpStatusCode : HttpStatusCode.InternalServerError));
                serverBase.AutoLogger.LogText(data);
                return result;
            }
            //else if (result.CallbackInfo.IsAccessDenied)
            //{
            //    //data = newLine + result.CallbackInfo.Data + newLine;
            //    await RunHttpActionResult(client, result.CallbackInfo.Data, client, serverBase);
            //}
            //MethodsCallHandler.BeginHttpMethodCallAction?.Invoke(client, callGuid, address, method, valueitems);
            //service = Activator.CreateInstance(RegisteredHttpServiceTypes[address]);
            if (result.ServiceInstance is IHttpClientInfo)
            {
                ((IHttpClientInfo)result.ServiceInstance).RequestHeaders = client.RequestHeaders;
                ((IHttpClientInfo)result.ServiceInstance).ResponseHeaders = client.ResponseHeaders;
                ((IHttpClientInfo)result.ServiceInstance).IPAddressBytes = client.IPAddressBytes;
            }
            if (serverBase.ProviderSetting.HttpSetting.HandleCrossOriginAccess)
            {
                AddOriginHeader(client, serverBase);
            }

            FillReponseHeaders(client, result.Context);
            if (result.StreamInfo != null)
            {
                result.FileActionResult = new FileActionResult(result.StreamInfo.Stream);
                if (!client.ResponseHeaders.ContainsKey("content-length") && result.StreamInfo.Length.HasValue)
                    client.ResponseHeaders.Add("Content-Length", result.StreamInfo.Length.Value);
                if (!client.ResponseHeaders.ContainsKey("content-type") && !string.IsNullOrEmpty(result.StreamInfo.ContentType))
                    client.ResponseHeaders.Add("Content-Type", result.StreamInfo.ContentType);
                if (!client.ResponseHeaders.ContainsKey("content-disposition") && !string.IsNullOrEmpty(result.StreamInfo.FileName))
                    client.ResponseHeaders.Add("Content-Disposition", $"attachment; filename={result.StreamInfo.FileName}");
                if (result.StreamInfo.Status.HasValue)
                    client.Status = result.StreamInfo.Status.Value;
            }
            if (result.FileActionResult != null)
                await RunHttpActionResult(client, result.FileActionResult, client, serverBase);
            else if (result.CallbackInfo.Data == null)
            {
                //data = newLine + $"result from method invoke {methodName}, is null " + address + newLine;
                await SendInternalErrorMessage(new Exception($"result from method invoke {methodName} , is null"), address, result.ServiceType, method, serverBase, client, newLine, HttpStatusCode.InternalServerError);
                serverBase.AutoLogger.LogText("RunHttpGETRequest : " + data);
            }
            else
            {
                await RunHttpActionResult(client, result.CallbackInfo.Data, client, serverBase);
            }

            return result;
        }

        internal static async Task RunHttpActionResult(IHttpClientInfo controller, object result, ClientInfo client, ServerBase serverBase)
        {
            try
            {

                //string response = $"HTTP/1.1 {(int)controller.Status} {HttpRequestController.GetStatusDescription((int)controller.Status)}" + newLine;

                //foreach (string item in headers)
                //{
                //    response += item + ": " + headers[item] + newLine;
                //}

                if (result is FileActionResult && controller.Status == HttpStatusCode.OK)
                {
                    //response += controller.ResponseHeaders.ToString();
                    FileActionResult file = (FileActionResult)result;
                    long fileLength = -1;
                    long position = 0;
                    //string len = "";
                    try
                    {
                        fileLength = file.FileStream.Length;
                        //len = "Content-Length: " + fileLength;
                    }
                    catch
                    {
                        try
                        {
                            fileLength = long.Parse(controller.ResponseHeaders["content-length"].First());
                        }
                        catch (Exception)
                        {

                        }
                    }
                    try
                    {
                        position = file.FileStream.Position;
                    }
                    catch
                    {

                    }

                    //response += len + newLine;
                    //byte[] bytes = System.Text.Encoding.ASCII.GetBytes(response);
                    await SendResponseHeadersToClient(controller.Status, controller.ResponseHeaders, client, 0);
                    //List<byte> allb = new List<byte>();
                    //if (file.FileStream.CanSeek)
                    //    file.FileStream.Seek(0, System.IO.SeekOrigin.Begin);
                    while (fileLength != position)
                    {
                        byte[] data = new byte[1024 * 20];
                        int readCount = await file.FileStream.ReadAsync(data, 0, data.Length);
                        if (readCount == 0)
                            break;
                        byte[] bytes = data.ToList().GetRange(0, readCount).ToArray();
                        await client.ClientStream.WriteAsync(bytes, 0, bytes.Length);
                    }
                    //delay to fix fast dispose before client read data
                    await Task.Delay(1000);
                    file.FileStream.Dispose();
                }
                else
                {
                    string data = null;
                    if (result is ActionResult actionResult)
                    {
                        if (actionResult.Data is System.IO.Stream)
                            data = "";
                        else
                            data = actionResult.Data is string ? actionResult.Data.ToString() : ServerSerializationHelper.SerializeObject(actionResult.Data, serverBase);
                        //if (!controller.ResponseHeaders.ContainsKey("content-length"))
                        //    controller.ResponseHeaders.Add("Content-Length", (System.Text.Encoding.UTF8.GetByteCount(data)).ToString().Split(','));

                        if (!controller.ResponseHeaders.ContainsKey("Content-Type"))
                        {
                            if (((ActionResult)result).Data is string)
                                controller.ResponseHeaders.Add("Content-Type", "text/html; charset=utf-8".Split(','));
                            else
                                controller.ResponseHeaders.Add("Content-Type", "application/json; charset=utf-8".Split(','));
                        }
                    }
                    else
                    {
                        if (result is System.IO.Stream)
                            data = "";
                        else
                            data = result is string ? (string)result : ServerSerializationHelper.SerializeObject(result, serverBase);
                        //if (!controller.ResponseHeaders.ContainsKey("content-length"))
                        //    controller.ResponseHeaders.Add("Content-Length", (System.Text.Encoding.UTF8.GetByteCount(data)).ToString().Split(','));

                        if (!controller.ResponseHeaders.ContainsKey("Content-Type"))
                        {
                            //if (result.Data is string)
                            //    controller.ResponseHeaders.Add("Content-Type", "text/html; charset=utf-8");
                            //else
                            controller.ResponseHeaders.Add("Content-Type", "application/json; charset=utf-8".Split(','));
                        }
                    }

                    if (!controller.ResponseHeaders.ContainsKey("Connection"))
                        controller.ResponseHeaders.Add("Connection", "close".Split(','));

                    byte[] dataBytes = Encoding.UTF8.GetBytes(data);
                    if (serverBase.EncryptResponse != null)
                    {
                        SignalGo.Shared.Log.AutoLogger.Default.LogText("EncryptResponse : " + dataBytes.Length);
                        var bytes = serverBase.EncryptResponse(client, dataBytes);

                        if (bytes != null)
                        {

                            try { controller.ResponseHeaders.Remove("Content-Type"); }
                            catch (Exception e) { }
                            dataBytes = bytes;
                            controller.ResponseHeaders.Add("Content-Type", "application/text; charset=utf-8".Split(','));
                        }

                        SignalGo.Shared.Log.AutoLogger.Default.LogText("EncryptResponse2 : " + dataBytes.Length);
                    }
                    await SendResponseHeadersToClient(controller.Status, controller.ResponseHeaders, client, dataBytes.Length);
                    await SendResponseDataToClient(dataBytes, client);
                }
            }
            catch (Exception ex)
            {

            }
        }


        /// <summary>
        /// run method of server http class with address and headers
        /// </summary>
        /// <param name="address">address</param>
        /// <param name="headers">headers</param>
        /// <param name="client">client</param>
        internal static async Task RunPostHttpRequestFile(string address, string httpMethod, string content, HttpClientInfo client, ServerBase serverBase)
        {
            string newLine = TextHelper.NewLine;
            string fullAddress = address;
            address = address.Trim('/');
            List<string> lines = address.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            //if (lines.Count <= 1)
            //{
            //    string msg = newLine + "SignalGo Error: method not found from address: " + address + newLine;
            //    await SendInternalErrorMessage(msg, serverBase, client, newLine, HttpStatusCode.InternalServerError);
            //    serverBase.AutoLogger.LogText(msg);
            //}
            //else
            //{
            string methodName = lines.Last();
            string parameters = "";
            if (methodName.Contains("?"))
            {
                string[] sp = methodName.Split(new[] { '?' }, 2);
                methodName = sp.First();
                parameters = sp.Last();
            }
            Dictionary<string, string> multiPartParameter = new Dictionary<string, string>();

            int len = int.Parse(client.GetRequestHeaderValue("content-length"));
            HttpPostedFileInfo fileInfo = null;
            if (content.Length < len)
            {
                string boundary = client.GetRequestHeaderValue("content-type").Split('=').Last();
                if (!boundary.Contains("--"))
                    boundary = null;
                int fileHeaderCount = 0;
                Tuple<int, string, string> res = await GetHttpFileFileHeader(client.ClientStream, boundary, len);
                fileHeaderCount = res.Item1;
                boundary = res.Item2;
                string response = res.Item3;

                //boundary = boundary.TrimStart('-');
                string contentType = "";
                string fileName = "";
                string name = "";
                bool findFile = false;
                string[] lineBreaks = new string[] { boundary.Replace("\"", ""), boundary.Replace("\"", "") + "--", "--" + boundary.Replace("\"", ""), "--" + boundary.Replace("\"", "") + "--" };
                foreach (string httpData in response.Split(lineBreaks, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (httpData.ToLower().Contains("content-disposition"))
                    {
                        if (httpData.Replace(" ", "").Contains(";filename="))
                        {
                            foreach (string header in httpData.Split(new string[] { TextHelper.NewLine }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                int index = header.ToLower().IndexOf("content-type: ");
                                if (index == 0)
                                {
                                    int ctypeLen = "content-type: ".Length;
                                    contentType = header.Substring(ctypeLen, header.Length - ctypeLen);
                                }
                                else if (header.ToLower().IndexOf("content-disposition:") == 0)
                                {
                                    CustomContentDisposition disp = new CustomContentDisposition(header);
                                    if (disp.Parameters.ContainsKey("filename"))
                                        fileName = disp.Parameters["filename"];
                                    if (disp.Parameters.ContainsKey("name"))
                                        name = disp.Parameters["name"];
                                }
                                findFile = true;
                            }
                            break;
                        }
                        else if (httpData.ToLower().Contains("content-disposition"))
                        {
                            if (httpData.Replace(" ", "").Contains(";name="))
                            {
                                string[] sp = httpData.Split(new string[] { TextHelper.NewLine + TextHelper.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                                string contentHeaders = sp.FirstOrDefault();
                                string datas = sp.LastOrDefault();
                                int index = contentHeaders.ToLower().IndexOf("content-disposition");
                                string header = contentHeaders.Substring(index);
                                int headLen = httpData.IndexOf(TextHelper.NewLine);
                                //header = sp.Length > 1 ? datas : data.Substring(index, headLen);
                                //var byteData = GoStreamReader.ReadBlockSize(client.TcpClient.GetStream(), (ulong)(len - content.Length - fileHeaderCount));
                                string newData = sp.Length > 1 ? datas : httpData.Substring(headLen + 4);//+ 4 Encoding.UTF8.GetString(byteData);
                                newData = newData.Trim(TextHelper.NewLine.ToCharArray());
                                //var newData = text.Substring(0, text.IndexOf(boundary) - 4);
                                if (header.ToLower().IndexOf("content-disposition:") == 0)
                                {
                                    CustomContentDisposition disp = new CustomContentDisposition(header.Trim().Split(new string[] { TextHelper.NewLine }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
                                    if (disp.Parameters.ContainsKey("name"))
                                        name = disp.Parameters["name"];
                                    //StringBuilder build = new StringBuilder();
                                    //using (var reader = new StringReader(newData))
                                    //{
                                    //    while (true)
                                    //    {
                                    //        var line = reader.ReadLine();
                                    //        if (line == null)
                                    //            break;
                                    //        else if (lineBreaks.Contains(line))
                                    //            continue;
                                    //        build.AppendLine(line);
                                    //    }
                                    //}
                                    multiPartParameter.Add(name, newData);
                                }
                            }
                        }
                        string[] keyValue = httpData.Split(new string[] { TextHelper.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                        if (keyValue.Length == 2)
                        {
                            if (!string.IsNullOrEmpty(parameters))
                            {
                                parameters += "&";
                            }
                            CustomContentDisposition disp = new CustomContentDisposition(keyValue[0]);
                            foreach (KeyValuePair<string, string> prm in disp.Parameters)
                            {
                                parameters += prm.Key;
                                parameters += "=" + prm.Value;
                            }

                        }
                    }
                }
                if (findFile)
                {
                    StreamGo stream = new StreamGo(client.ClientStream);
                    stream.SetOfStreamLength(len - content.Length - fileHeaderCount, boundary.Length + 12 - 6);// + 6 ; -6 ezafe shode
                    fileInfo = new HttpPostedFileInfo()
                    {
                        Name = name,
                        ContentLength = stream.Length,
                        ContentType = contentType,
                        FileName = fileName,
                        InputStream = stream
                    };
                }


                //byte[] buffer = new byte[len * 5];
                //var readCount = client.TcpClient.Client.Receive(buffer);
                //// I dont know why 44 bytes(overplus) always sent
                //var postResponse = Encoding.UTF8.GetString(buffer.ToList().GetRange(0, readCount).ToArray());
                //content = postResponse;
            }




            methodName = methodName.ToLower();
            lines.RemoveAt(lines.Count - 1);
            address = "";
            foreach (string item in lines)
            {
                address += item + "/";
            }
            address = address.TrimEnd('/').ToLower();
            //if (RegisteredHttpServiceTypes.ContainsKey(address))
            //{
            MethodInfo method = null;
            string callGuid = Guid.NewGuid().ToString();
            object serviceInstance = null;
            Type serviceType = null;
            string data = null;
            try
            {
                List<Shared.Models.ParameterInfo> values = new List<Shared.Models.ParameterInfo>();
                string jsonParameters = "";
                //var methods = (from x in RegisteredHttpServiceTypes[address].GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance) where x.Name.ToLower() == methodName && x.IsPublic && !(x.IsSpecialName && (x.Name.StartsWith("set_") || x.Name.StartsWith("get_"))) select x).ToList();
                //if (methods.Count == 0)
                //{
                //    string data = newLine + "SignalGo Error: Method name not found in method list: " + methodName + newLine;
                //    sendInternalErrorMessage(data);
                //    serverBase.AutoLogger.LogText(data);
                //    return;
                //}

                //List<Tuple<string, string>> values = new List<Tuple<string, string>>();
                if (multiPartParameter.Count > 0)
                {
                    foreach (KeyValuePair<string, string> item in multiPartParameter)
                    {
                        values.Add(new Shared.Models.ParameterInfo() { Name = item.Key, Value = item.Value });
                    }
                }
                else if (client.GetRequestHeaderValue("content-type") == "application/json")
                {
                    jsonParameters = parameters;
                    JObject des = JObject.Parse(parameters);
                    foreach (JProperty item in des.Properties())
                    {
                        JToken value = des.GetValue(item.Name);
                        //values.Add(new Tuple<string, string>(item.Name, Uri.UnescapeDataString(value.Value<string>())));
                        values.Add(new Shared.Models.ParameterInfo() { Name = item.Name, Value = value.ToString() });
                    }
                }
                else
                {
                    parameters = parameters.Trim('&');
                    if (!string.IsNullOrEmpty(parameters))
                    {
                        foreach (string item in parameters.Split(new[] { '&' }))
                        {
                            string[] keyValue = item.Split(new[] { '=' }, 2);
                            values.Add(new Shared.Models.ParameterInfo() { Name = keyValue.Length == 2 ? keyValue[0] : "", Value = Uri.UnescapeDataString(keyValue.Last()) });
                        }
                    }
                }



                CallMethodResultInfo<OperationContext> result = await CallHttpMethod(client, address, methodName, values, jsonParameters, serverBase, method, data, newLine, fileInfo, null);

                serviceType = result.ServiceType;
                serviceInstance = result.ServiceInstance;
                //valueitems = values.Select(x => x.Item2).ToList();
                //MethodsCallHandler.BeginHttpMethodCallAction?.Invoke(client, callGuid, address, method, valueitems);


            }
            catch (Exception ex)
            {
                // exception = ex;
                if (serverBase.ErrorHandlingFunction != null)
                {
                    ActionResult result = serverBase.ErrorHandlingFunction(ex, serviceType, method, client).ToActionResult();
                    await RunHttpActionResult(client, result, client, serverBase);
                }
                else
                {
                    //data = newLine + ex.ToString() + address + newLine;
                    await SendInternalErrorMessage(ex, address, serviceType, method, serverBase, client, newLine, HttpStatusCode.InternalServerError);
                }
                if (!(ex is SocketException))
                    serverBase.AutoLogger.LogError(ex, "RunPostHttpRequestFile");
            }
            finally
            {
                //ClientConnectedCallingCount--;
                //MethodsCallHandler.EndHttpMethodCallAction?.Invoke(client, callGuid, address, method, valueitems, result, exception);
            }
            //}
            //else
            //{
            //    string data = newLine + "SignalGo Error: address not found in signalGo services: " + address + newLine;
            //    sendInternalErrorMessage(data);
            //    AutoLogger.LogText(data);
            //}
            //}
        }


        /// <summary>
        /// send service reference data to client
        /// </summary>
        /// <param name="client"></param>
        internal static async Task SendSignalGoServiceReference(HttpClientInfo client, ServerBase serverBase)
        {
            try
            {
                PipeNetworkStream stream = client.ClientStream;

                Shared.Models.ServiceReference.NamespaceReferenceInfo referenceData = new ServiceReferenceHelper() { IsRenameDuplicateMethodNames = client.RequestHeaders["selectedLanguage"].FirstOrDefault() == "1" }.GetServiceReferenceCSharpCode(client.GetRequestHeaderValue("servicenamespace"), serverBase);
                string result = ServerSerializationHelper.SerializeObject(referenceData, serverBase);
                client.ResponseHeaders["Content-Type"] = "text/html; charset=utf-8".Split(',');
                client.ResponseHeaders["Service-Type"] = "SignalGoServiceType".Split(',');
                byte[] dataBytes = Encoding.UTF8.GetBytes(result);
                await SendResponseHeadersToClient(HttpStatusCode.OK, client.ResponseHeaders, client, dataBytes.Length);
                await SendResponseDataToClient(dataBytes, client);
            }
            catch (Exception ex)
            {

            }
        }



        internal static async Task<Tuple<int, string, string>> GetHttpFileFileHeader(PipeNetworkStream stream, string boundary, int maxLen)
        {
            List<byte> bytes = new List<byte>();
            byte findNextlvl = 0;
            string response = "";
            while (true)
            {
                byte singleByte = await stream.ReadOneByteAsync();
                bytes.Add(singleByte);
                if (bytes.Count >= maxLen)
                {
                    string data = Encoding.UTF8.GetString(bytes.ToArray());
                    response = data;
                    if (response.Contains("--") && string.IsNullOrEmpty(boundary))
                    {
                        string[] split = response.Split(new string[] { TextHelper.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string item in split)
                        {
                            if (response.Contains("--"))
                            {
                                boundary = item;
                                break;
                            }
                        }
                    }
                    return new Tuple<int, string, string>(bytes.Count, boundary, response);

                }
                if (findNextlvl > 0)
                {
                    if (findNextlvl == 1 && singleByte == 10)
                        findNextlvl++;
                    else if (findNextlvl == 2 && singleByte == 13)
                        findNextlvl++;
                    else if (findNextlvl == 3 && singleByte == 10)
                    {
                        string data = Encoding.UTF8.GetString(bytes.ToArray());
                        string res = data.Replace(" ", "").ToLower();

                        string[] lines = res.Split(new string[] { TextHelper.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                        bool canBreak = false;
                        foreach (string item in lines)
                        {
                            if (item.Trim().StartsWith("content-disposition:") && item.Contains("filename="))
                            {
                                canBreak = true;
                                break;
                            }
                        }
                        if (canBreak)
                            break;
                        findNextlvl = 0;
                    }
                    else
                        findNextlvl = 0;
                }
                else
                {
                    if (singleByte == 13)
                        findNextlvl++;
                }
            }
            response = Encoding.UTF8.GetString(bytes.ToArray());
            if (response.Contains("--") && string.IsNullOrEmpty(boundary))
            {
                string[] split = response.Split(new string[] { TextHelper.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string item in split)
                {
                    if (response.Contains("--"))
                    {
                        boundary = item;
                        break;
                    }

                }
                //if (lastEnter > 0)
                //{
                //    var startindex = response.LastIndexOf("--") + 2;
                //    boundary = response.Substring(startindex, lastEnter - startindex - 6);
                //}
            }
            return new Tuple<int, string, string>(bytes.Count, boundary, response);
        }

        internal static async Task SendInternalErrorMessage(Exception exception, string address, Type srviceType, MethodInfo methodInfo, ServerBase serverBase, HttpClientInfo client, string newLine, HttpStatusCode httpStatusCode)
        {
            try
            {
                //{ 500} {HttpRequestController.GetStatusDescription((int)HttpStatusCode.InternalServerError)}
                if (serverBase.ProviderSetting.HttpSetting.HandleCrossOriginAccess)
                {
                    AddOriginHeader(client, serverBase);
                }
                //string message = newLine + $"{msg}" + newLine;
                string message = "";

                client.ResponseHeaders["Content-Type"] = "text/html; charset=utf-8".Split(',');
                //responseHeaders.Add("Content-Length", (message.Length - 2).ToString());
                client.ResponseHeaders["Connection"] = "Close".Split(',');
                if (serverBase.ErrorHandlingFunction != null)
                {
                    message = newLine + $"{serverBase.ErrorHandlingFunction(exception, srviceType, methodInfo, client).SerializeObject(serverBase)}" + newLine;
                }
                else
                {
                    message = newLine + $"{address + ":" + exception.ToString()}" + newLine;
                }
                byte[] dataBytes = Encoding.UTF8.GetBytes(message);
                await SendResponseHeadersToClient(httpStatusCode, client.ResponseHeaders, client, dataBytes.Length);
                await SendResponseDataToClient(dataBytes, client);
            }
            catch (SocketException)
            {

            }
            catch (Exception ex)
            {
                serverBase.AutoLogger.LogError(ex, "RunHttpGETRequest sendErrorMessage");
            }
        }


        private static void FillReponseHeaders(HttpClientInfo client, OperationContext context)
        {
            foreach (object contextResult in OperationContextBase.GetAllHttpKeySettings(context))
            {
                foreach (var property in contextResult.GetType().GetListOfProperties().Select(x => new { Info = x, Attribute = x.GetCustomAttributes<HttpKeyAttribute>().GroupBy(y => y.KeyType) }))
                {
                    foreach (IGrouping<HttpKeyType, HttpKeyAttribute> group in property.Attribute)
                    {
                        if (group.Key == HttpKeyType.Cookie)
                        {
                            foreach (HttpKeyAttribute httpKey in group.ToList())
                            {
                                if (!client.ResponseHeaders.ContainsKey(httpKey.ResponseHeaderName))
                                {
                                    client.ResponseHeaders[httpKey.ResponseHeaderName] = new string[] { OperationContextBase.IncludeValue((string)property.Info.GetValue(contextResult, null), httpKey.KeyName, httpKey.HeaderValueSeparate, httpKey.HeaderKeyValueSeparate) + httpKey.Perfix };
                                }
                            }
                        }
                    }
                }
            }
        }


        public static async Task SendResponseHeadersToClient(HttpStatusCode httpStatusCode, IDictionary<string, string[]> webResponseHeaderCollection, ClientInfo client, int contentLength)
        {

            string newLine = TextHelper.NewLine;
            string firstLine = "";
            firstLine = $"HTTP/1.1 {(int)httpStatusCode} {HttpRequestController.GetStatusDescription((int)httpStatusCode)}" + newLine;

            if (!webResponseHeaderCollection.ContainsKey("Content-Type"))//|| !webResponseHeaderCollection["Content-Type"].Contains("utf-8")
            {
                webResponseHeaderCollection["Content-Type"] = "text/html; charset=utf-8".Split(',');
            }
            if (!webResponseHeaderCollection.ContainsKey("Content-Length") || webResponseHeaderCollection["Content-Length"].FirstOrDefault() == "0")
                webResponseHeaderCollection["Content-Length"] = (contentLength).ToString().Split(',');
            else
            {

            }
            if (client.IsOwinClient && client is HttpClientInfo httpClient)
            {
                httpClient.ChangeStatusCode(httpClient.Status);
                return;
            }
            StringBuilder builder = new StringBuilder();
            foreach (KeyValuePair<string, string[]> item in webResponseHeaderCollection)
            {
                if (item.Value == null)
                    builder.AppendLine(item.Key + ": ");
                else
                    builder.AppendLine(item.Key + ": " + string.Join(",", item.Value));
            }
            builder.AppendLine();

            byte[] headBytes = Encoding.UTF8.GetBytes(firstLine + builder.ToString());
            await client.StreamHelper.WriteToStreamAsync(client.ClientStream, headBytes);
        }


        internal static async Task SendResponseDataToClient(byte[] dataBytes, ClientInfo client)
        {
            await client.StreamHelper.WriteToStreamAsync(client.ClientStream, dataBytes);
        }
    }
}
