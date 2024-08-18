using PhiBasicTranslator.GeneralUtilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public class PhiCodebase
    {
        public string Name = "Default_Codebase";
        public PhiCodebase() { }

        public List<PhiClass> ClassList { get; set; } = new List<PhiClass>();

        public void Save(string File)
        {
            FileManager.SaveToFile(File, this);
        }

        public static PhiCodebase Load(string File)
        {
            return FileManager.LoadFromFile<PhiCodebase>(File);
        }

        public void ExportASM(string Folder)
        {
            if (Directory.Exists(Folder))
            {
                string fld = Folder + "\\" + Name;

                if(!Directory.Exists(fld))
                    Directory.CreateDirectory(fld);

                foreach (PhiClass file in ClassList)
                {
                    File.WriteAllLines(fld + "\\" + file.Name, file.translatedASM);
                }
            }
        }

        public PhiCodebase Copy()
        {
            List<PhiClass> clst = new List<PhiClass>();

            foreach (PhiClass cl in ClassList) clst.Add(cl.Copy());

            return new PhiCodebase
            {
                ClassList = clst
            };
        }
    }
}
