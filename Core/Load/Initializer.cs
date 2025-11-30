using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core
{
    public class Initializer
    {
        public Action<float, string> OnProgress { get; set; }

        public ProvinceSystem ProvinceSystem { get; set; }
        public async Task LoadAsync()
        {

        }
    }
}
