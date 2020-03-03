using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using ServiceStack;
using ServiceStack.OrmLite;
using ServiceStack.OrmLite.Dapper;


namespace SQLMap
{
    public class SQLMap
    {
        public void Test(string path)
        {

            
            var dbFactory = new OrmLiteConnectionFactory($"{path}",SqliteDialect.Provider);

            

            using (var db = dbFactory.Open())
            {

                var foo = db.Query<dynamic>("SELECT FirstName,LastName from Customers");

                var bar = (IDictionary<string, object>) foo.First();
                
                Console.WriteLine($"{string.Join(",",bar.Keys)}");

                using (var writer = new StringWriter())
                {
                    using (var csv = new CsvWriter(writer,CultureInfo.InvariantCulture))
                    {
                        csv.WriteDynamicHeader(foo.First());
                        csv.NextRecord();
                        
                        // foreach(IDictionary<string, object> row in foo) {
                        //     Console.WriteLine("row:");
                        //     foreach(var pair in row) {
                        //         Console.WriteLine("  {0} = {1}", pair.Key, pair.Value);
                        //     }
                        // }

                        //csv.WriteRecords(foo);
                        
                        Debug.WriteLine(writer.ToString());
                    }
                }
                


                /*foreach (var o in foo)
                {
                    Debug.WriteLine(((object)o.FirstName).ToString());
                }*/

            }


            //     sqlite_cmd.CommandText = ;


       
            
                
        }
    }
}
