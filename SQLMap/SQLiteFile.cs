using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Serilog;

namespace SQLMaps;

    public static class SqLiteFile
    {
        private const long Sig = 0x66206574694c5153;

        public static bool IsSqLiteFile(string path)
        {
            if (File.Exists(path) == false)
            {
                Log.Warning("{Path} not found!",path);
            }

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                if (fs.Length < 8)
                {
                    return false;
                }

                using (var br = new BinaryReader(fs))
                {
                    var fileSig = br.ReadInt64();

                    return fileSig == Sig;
                }
            }
            catch (Exception a)
            {
                Log.Error("Error accessing {Path}: {Message}",path,a.Message);
                return false;
            }
        }
    }



