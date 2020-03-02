using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SQLMap
{
    public static class SQLiteFile
    {
        private const long Sig = 0x66206574694c5153;

        public static bool IsSQLiteFile(string path)
        {
            if (File.Exists(path) == false)
            {
                throw new FileNotFoundException($"'{path}' not found");
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);

            if (fs.Length < 8)
            {
                return false;
            }

            using var br = new BinaryReader(fs);
            var fileSig = br.ReadInt64();

            return fileSig == Sig;
        }


    }
}


