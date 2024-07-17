using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.GeneralUtilities
{
    internal class FileManager
    {
        public static T LoadFromFile<T>(string fileName)
        {
            try
            {
                string jsonContent = File.ReadAllText(fileName);
                return JsonConvert.DeserializeObject<T>(jsonContent);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }

            return default(T); //returns null
        }

        public static void SaveToFile<T>(string fileName, T obj)
        {
            string content = JsonConvert.SerializeObject(obj);

            File.WriteAllText(fileName, content);
        }
    }
}
