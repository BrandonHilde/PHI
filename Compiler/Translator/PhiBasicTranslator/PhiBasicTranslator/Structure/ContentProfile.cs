using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public enum Inside { MultiComment, Comment, String, None };
    public class ContentProfile
    {
        public Inside[] ContentInside = { Inside.None };
        public ContentProfile(int len) 
        { 
            ContentInside = new Inside[len];
        }
    }
}
