using System;
using SQLite;

namespace SQLMap
{
    public class SQLMap
    {
        public void Test(string path)
        {
            var db = new SQLiteConnection (path);
        }
    }
}
