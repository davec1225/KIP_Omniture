using System;
using System.IO;
using System.Net;
using System.Text;
using System.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;
using System.Security.Cryptography;

public enum HttpVerb
{
    GET,
    POST,
    PUT,
    DELETE
}

namespace KIP_Omniture
{
    class Program
    {

        static void Main(string[] args)
        {
            String json;
            DateTime runDateTime = DateTime.UtcNow;                   // Not Run all
            //DateTime runDateTime = new DateTime(2015, 8, 26);           // Run all
            int runHour = 0;
            String city = String.Empty;
            String country = String.Empty;
            Decimal latitude = 0;
            Decimal longitude = 0;


            //while (runDateTime < DateTime.Now.AddDays(-1))                  // Run all
            //{                                                               // Run all
            // We want the prevous hours data
            // If it's zero, we want the last hour of yesterday
            if (runDateTime.Hour == 0)
            {
                runDateTime = runDateTime.AddDays(-1);
                runHour = 23;
            }
            else
                runHour = runDateTime.Hour - 1;

            // Change run date time to zero minutes,secomnds
            runDateTime = new DateTime(runDateTime.Year, runDateTime.Month, runDateTime.Day, runHour, 0, 0, 0);

            //runHour = 0;                                                  // Run all

            // Run report and return report ID
            string reportValue = QueueOmnitureQueueReport(runDateTime);
            
            // We will wait for 10*30 seconds for the report to be ready
            int attemp = 10;
            Boolean reportReady = true;
            do
            {
                reportReady = true;
                json = GetOmnitureQueueReport(reportValue);
                if (json.Contains("(400) Bad Request"))
                {
                    Thread.Sleep(30000);            // wait 10 seconds and try again
                    reportReady = false;
                    attemp--;
                }
            } while (attemp > 0 && !reportReady);

            if (attemp != 0)
            {
                XmlDocument xd = new XmlDocument();
                xd = (XmlDocument)JsonConvert.DeserializeXmlNode("{\"Root\":" + json + "}");
                DataSet ds = new DataSet();
                ds.ReadXml(new XmlNodeReader(xd));
                AwarenessDataClassesDataContext dc = new AwarenessDataClassesDataContext();

                //for (int i = 0; i < 24; i++)                // Run all
                //{                                           // Run all
                //    runHour = i;                            // Run all
                    DataRow[] dataRows = ds.Tables["data"].Select(string.Format("[hour]='{0}'", runHour), "", DataViewRowState.CurrentRows);
                    foreach (DataRow dataRow in dataRows)
                    {
                        if (ds.Tables["breakdown"] != null)
                        {
                            DataRow[] breakdownRows = ds.Tables["breakdown"].Select(String.Format("[data_Id]={0}", dataRow["data_Id"]));
                            foreach (DataRow breakdownRow in breakdownRows)
                            {
                                string name = breakdownRow["name"].ToString();
                                string url = breakdownRow["url"].ToString();
                                string geo_city = string.Empty;
                                string geo_country = string.Empty;

                                DataRow[] countsRows = ds.Tables["counts"].Select(String.Format("[breakdown_Id]={0}", breakdownRow["breakdown_Id"]));
                                Int32 count0 = Convert.ToInt32(countsRows[0][0]);
                                Int32 count1 = Convert.ToInt32(countsRows[1][0]);
                                Int32 count2 = Convert.ToInt32(countsRows[2][0]);
                                if (count0 > 0 || count1 > 0 || count2 > 0)           // Save the row.
                                {
                                    city = String.Empty;
                                    country = String.Empty;
                                    latitude = 0;
                                    longitude = 0;

                                    GetCountryCity(name, ref city, ref country);

                                    // Se if the city is a country capitial
                                    GetCaptialLatitudeLongitude(city, country, ref latitude, ref longitude, ref geo_city, ref geo_country);
                                    // If not see if you can find the city in the CountryCity table
                                    if (latitude == 0 && longitude == 0)
                                        GetCityLatitudeLongitude(city, country, ref latitude, ref longitude, ref geo_city, ref geo_country);
                                    // If we've still not found the city/country then just get the capital for that country
                                    if (latitude == 0 && longitude == 0)
                                        GetCaptialLatitudeLongitude(country, ref latitude, ref longitude, ref geo_city, ref geo_country);
                                    // if we still don't have ac lat/long then list in under "Unknown"
                                    if (latitude == 0 && longitude == 0)
                                    {
                                        geo_city = "Unknown";
                                        geo_country = "Unknown";
                                    }

                                    string line = string.Format("Date/Time: {0}, Country: {1}, URL: {2}, pageViews: {3}, visits: {4}", runDateTime.ToString(""), name, url, count0, count1);
                                    Console.WriteLine(line);

                                    omniture_raw_count row = new omniture_raw_count();
                                    row.created_date = runDateTime;                                // Not Run all
                                    //row.created_date = runDateTime.AddHours(runHour);                // Run all
                                    row.City = city;
                                    row.Country = country;
                                    row.pageViews = count0;
                                    row.visits = count1;
                                    row.uniquevisitors = count2;
                                    row.latitude = latitude;
                                    row.longitude = longitude;
                                    row.Geo_City = geo_city;
                                    row.Geo_Country = geo_country;
                                    dc.omniture_raw_counts.InsertOnSubmit(row);
                                }
                            }
                        }
                    }
                    dc.SubmitChanges();
                    update_totals();         // not Run all
                //}                              // Run all
                //update_totals();             // Run all
            }
            //runDateTime = runDateTime.AddDays(1);      // Run all
            //}                                          // Run all
        }

        static void update_totals()
        {
            AwarenessDataClassesDataContext dc = new AwarenessDataClassesDataContext();

            System.Linq.IQueryable<omniture_count> response = from result in dc.omniture_counts
                                        where 1 == 1
                                        select result;

            foreach (omniture_count row in response)
            {
                try
                {
                    Console.WriteLine(String.Format("{0} - {1}\r\n", row.Geo_Country, row.Count));
                    Geo_Total gt = (from c in dc.Geo_Totals
                                    where c.Geo_Country == row.Geo_Country
                                    select c).Single();
                    gt.Omniture = row.Count;
                    dc.SubmitChanges();
                }
                catch (InvalidOperationException io)
                {
                    Console.WriteLine(io.Message);
                }
            }
            dc.SubmitChanges();
        }

        static string QueueOmnitureQueueReport(DateTime runDateTime)
        {
            HttpVerb Method = HttpVerb.POST;
            string ContentType = "application/json";
            //string PostDataFormat = "{\"reportDescription\":{\"reportSuiteID\":\"npagkeepitpumpingprod\",\"date\":\"[RUNDATE]\",\"dateGranularity\":\"hour\",\"metrics\":[{\"id\":\"pageViews\"}],\"elements\":[{\"id\":\"geoRegion\"}]}}";
            //string PostDataFormat = "{\"reportDescription\":{\"reportSuiteID\":\"npagkeepitpumpingprod\",\"date\":\"[RUNDATE]\",\"dateGranularity\":\"hour\",\"metrics\":[{\"id\":\"pageViews\"},{\"id\":\"visits\"}],\"elements\":[{\"id\":\"geoRegion\"}]}}";
            string PostDataFormat = "{\"reportDescription\":{\"reportSuiteID\":\"npagkeepitpumpingprod\",\"date\":\"[RUNDATE]\",\"dateGranularity\":\"hour\",\"metrics\":[{\"id\":\"pageViews\"},{\"id\":\"visits\"},{\"id\":\"uniquevisitors\"}],\"elements\":[{\"id\":\"geoRegion\"},{\"id\":\"geoRegion\"},{\"id\":\"geoRegion\"}]}}";
            string PostData = PostDataFormat.Replace("[RUNDATE]", runDateTime.ToString("yyyy-MM-dd"));

            string x_wsse = buildWsse(Properties.Settings.Default.USERNAME, Properties.Settings.Default.SECRET, DateTime.UtcNow);

            string responseValue = string.Empty;
            var request = (HttpWebRequest)WebRequest.Create(Properties.Settings.Default.ENDPOINT + "?method=Report.Queue");

            request.Method = Method.ToString();
            request.Headers.Add(x_wsse);
            request.ContentLength = 0;
            request.ContentType = ContentType;

            if (!string.IsNullOrEmpty(PostData) && Method == HttpVerb.POST)
            {
                var encoding = new UTF8Encoding();
                var bytes = UTF8Encoding.UTF8.GetBytes(PostData);
                request.ContentLength = bytes.Length;

                using (var writeStream = request.GetRequestStream())
                {
                    writeStream.Write(bytes, 0, bytes.Length);
                }
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            {

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var message = String.Format("Request failed. Received HTTP {0}", response.StatusCode);
                    throw new ApplicationException(message);
                }

                // grab the response
                using (var responseStream = response.GetResponseStream())
                {
                    if (responseStream != null)
                        using (var reader = new StreamReader(responseStream))
                        {
                            responseValue = reader.ReadToEnd();
                        }
                }
            }


            return responseValue;
        }

        static string GetOmnitureQueueReport(string reportValue)
        {
            HttpVerb Method = HttpVerb.POST;
            string ContentType = "application/x-www-form-urlencoded";
            string responseValue = string.Empty;

            string x_wsse = buildWsse(Properties.Settings.Default.USERNAME, Properties.Settings.Default.SECRET, DateTime.UtcNow);

            var request = (HttpWebRequest)WebRequest.Create(Properties.Settings.Default.ENDPOINT + "?method=Report.Get");

            request.Method = Method.ToString();
            request.Headers.Add(x_wsse);
            request.ContentLength = 0;
            request.ContentType = ContentType;

            if (!string.IsNullOrEmpty(reportValue) && Method == HttpVerb.POST)
            {
                var encoding = new UTF8Encoding();
                var bytes = UTF8Encoding.UTF8.GetBytes(reportValue);
                request.ContentLength = bytes.Length;

                using (var writeStream = request.GetRequestStream())
                {
                    writeStream.Write(bytes, 0, bytes.Length);
                }
            }

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {

                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        var message = String.Format("Request failed. Received HTTP {0}", response.StatusCode);
                        throw new ApplicationException(message);
                    }

                    // grab the response
                    using (var responseStream = response.GetResponseStream())
                    {
                        if (responseStream != null)
                            using (var reader = new StreamReader(responseStream))
                            {
                                responseValue = reader.ReadToEnd();
                            }
                    }
                }
            }
            catch(InvalidOperationException oe)
            {
                responseValue = oe.Message;
            }

            return responseValue;
        }

        // Find the latitude/longitude based on City/Country name using the country capital table
        static void GetCaptialLatitudeLongitude(String city, String country, ref decimal latitude, ref decimal longitude, ref string geo_city, ref string geo_country)
        {
            WorldDataClassesDataContext dc = new WorldDataClassesDataContext();
            latitude = 0;
            longitude = 0;
            geo_city = String.Empty;
            geo_country = String.Empty;
            CountryCapital cc;

            try
            {
                cc = (from c in dc.CountryCapitals
                        where c.Country == country && c.City == city
                        select c).Single();
            }
            catch (InvalidOperationException io)
            {
                Console.WriteLine(io.Message);
                return;
            }

            geo_city = cc.City;
            geo_country = cc.Country;
            latitude = (decimal) cc.Latitude;
            longitude = (decimal) cc.Longitude;
        }

        // Find the latitude/longitude based on Country name using the country capital table
        static void GetCaptialLatitudeLongitude(String country, ref decimal latitude, ref decimal longitude, ref string geo_city, ref string geo_country)
        {
            WorldDataClassesDataContext dc = new WorldDataClassesDataContext();
            latitude = 0;
            longitude = 0;
            geo_city = String.Empty;
            geo_country = String.Empty;
            CountryCapital cc;

            try
            {
                cc = (from c in dc.CountryCapitals
                      where c.Country == country
                      select c).Single();
            }
            catch (InvalidOperationException io)
            {
                Console.WriteLine(io.Message);
                return;
            }

            geo_city = cc.City;
            geo_country = cc.Country;
            latitude = (decimal)cc.Latitude;
            longitude = (decimal)cc.Longitude;
        }

        // Find the latitude/longitude based on City/Country name using the country/city view table
        static void GetCityLatitudeLongitude(String city, String country, ref decimal latitude, ref decimal longitude, ref string geo_city, ref string geo_country)
        {
            WorldDataClassesDataContext dc = new WorldDataClassesDataContext();
            latitude = 0;
            longitude = 0;
            geo_city = String.Empty;
            geo_country = String.Empty;
            CountryCity cc;

            try
            {
                cc = (from c in dc.CountryCities
                        where c.Country == country && c.City == city
                        select c).First();
            }
            catch (InvalidOperationException io)
            {
                Console.WriteLine(io.Message);
                return;
            }

            geo_city = cc.City;
            geo_country = cc.Country;
            latitude = (decimal)cc.Latitude;
            longitude = (decimal)cc.Longitude;
        }

        // Parse out City & Country from "City Name (Country Name)"
        static private void GetCountryCity(string countryCity, ref string country, ref string city)
        {
            int op = countryCity.IndexOf('(');
            int cp = countryCity.IndexOf(')', op);
            if (op == -1 || cp != -1)
            {
                op++;
                cp = cp - op;
                country = countryCity.Substring(0, op - 1).Trim();
                city = countryCity.Substring(op, cp);
            }
        }

        static string buildWsse(String username, String secret, DateTime startRun)
        {
            string wsse = string.Empty;
            string utcCreated = startRun.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string nonce = generateNonce(24); //startRun
            string digest = getBase64(nonce + utcCreated + secret);
            string nonce64 = generateNonce64(nonce);

            wsse = String.Format("X-WSSE: UsernameToken Username=\"{0}\", PasswordDigest=\"{1}\", Nonce=\"{2}\", Created=\"{3}\"", username, digest, nonce64, utcCreated);

            return wsse;
        }

        static string generateNonce64(String nonce)
        {
            byte[] work = Encoding.ASCII.GetBytes(nonce);
            string nonce64 = Convert.ToBase64String(work).ToString();
            return nonce64;
        }

        // This works, but it's not being used (alternet method)
        //static string generateNonce(DateTime startRun)
        //{
        //    byte[] work = Encoding.ASCII.GetBytes(startRun.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        //    string nonce = Convert.ToBase64String(work).ToString();
        //    return nonce;
        //}

        private static string validChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        private static Random random = new Random();
        private static string generateNonce(int length)
        {
            var nonceString = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                nonceString.Append(validChars[random.Next(0, validChars.Length - 1)]);
            }

            return nonceString.ToString();
        }

        //static string generateTimestamp()
        //{
        //    return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        //}

        static string getBase64(string input)
        {
            //Digest = Base64( SHA1( Nonce + CurrentTimestamp + Secret));
            SHA1 sha = new SHA1Managed();
            ASCIIEncoding ae = new ASCIIEncoding();
            byte[] data = ae.GetBytes(input);
            byte[] output = sha.ComputeHash(data);
            return Convert.ToBase64String(output);
        }
    }
}
