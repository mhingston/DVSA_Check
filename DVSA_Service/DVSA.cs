using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scriban;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace DVSA_Service
{
    // Example config JSON string
    //{
    //  "apiUrl": "https://beta.check-mot.service.gov.uk/trade/vehicles/mot-tests?registration={{reg_no}}",
    //  "requestHeaders": [
    //    {
    //      "x-api-key": "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"
    //    }
    //  ]
    //}
    public class DVSA
    {
        private readonly JObject config;
        private readonly HttpClient client;
        public enum Status { Complete = 3, NotFound, HttpError };

        public DVSA(string config)
        {
            this.config = JObject.Parse(config);
            client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public async Task<string> LookupAsync(string regNo, bool isNorthernIrelandSite)
        {
            DateTime motDate;
            Template template = Template.Parse(config["apiUrl"].ToString());
            string ApiUrl = template.Render(new
            {
                RegNo = Uri.EscapeUriString(regNo)
            });

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);

            foreach (JObject obj in config["requestHeaders"])
            {
                foreach (JProperty property in obj.Properties())
                {
                    request.Headers.Add(property.Name, property.Value.ToString());
                }
            }

            JObject result = new JObject();

            using (HttpResponseMessage response = await client.SendAsync(request))
            {
                try
                {
                    string content = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode >= HttpStatusCode.BadRequest)
                    {
                        HttpRequestException error = new HttpRequestException("Bad request");
                        error.Data.Add("StatusCode", response.StatusCode);
                        throw error;
                    }

                    JContainer body = (JContainer)JsonConvert.DeserializeObject(content);

                    try
                    {
                        if (body[0]["motTestExpiryDate"] != null) // this property should only exist for vehicles without an MOT history, i.e. new vehicles
                        {
                            motDate = DateTime.ParseExact(body[0]["motTestExpiryDate"].ToString(),
                                "yyyy-MM-dd", CultureInfo.InvariantCulture);

                            // The DVSA API seems to used RegDate + 3 years for date of first MOT so we need to be able to override this for NI sites
                            if (isNorthernIrelandSite == true)
                            {
                                result.Add("MOTDueDate", motDate.AddYears(1));
                            }

                            else
                            {
                                result.Add("MOTDueDate", motDate);
                            }
                        }

                        else // this is for vehicles with an MOT history
                        {
                            motDate = DateTime.ParseExact(body[0]["motTests"][0]["expiryDate"].ToString(), "yyyy.MM.dd", CultureInfo.InvariantCulture);
                            result.Add("MOTDueDate", motDate);
                        }
                    }

                    catch (Exception error)
                    {
                        result.Add("MOTDueDate", null);
                    }

                    try
                    {
                        if (body[0]["firstUsedDate"] != null)
                        {
                            result.Add("RegDate", DateTime.ParseExact(body[0]["firstUsedDate"].ToString(), "yyyy.MM.dd", CultureInfo.InvariantCulture));
                        }

                        else if (result["MOTDueDate"] != null && body[0]["manufactureYear"] != null) // if there is no first used date determine the registration date from MOT date - manufacturerYear
                        {
                            motDate = Convert.ToDateTime(result["MOTDueDate"]);
                            result.Add("RegDate", motDate.AddYears(Convert.ToInt32(body[0]["manufactureYear"]) - motDate.Year));
                        }
                    }

                    catch (Exception error)
                    {
                        result.Add("RegDate", null);
                    }

                    if (result["MOTDueDate"] != null || result["RegDate"] != null)
                    {
                        result.Add("StatusID", Convert.ToInt32(Status.Complete));
                    }

                    else
                    {
                        result.Add("StatusID", Convert.ToInt32(Status.NotFound));
                    }
                }

                catch (Exception error)
                {
                    if (Convert.ToInt32(error.Data["StatusCode"]) == Convert.ToInt32(HttpStatusCode.NotFound))
                    {
                        result.Add("StatusID", Convert.ToInt32(Status.NotFound));
                    }

                    else
                    {
                        result.Add("StatusID", Convert.ToInt32(Status.HttpError));
                    }
                }
            }

            return result.ToString();
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
