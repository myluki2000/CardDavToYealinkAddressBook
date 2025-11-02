using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CardDavToYealinkAddressBook
{
    internal class Configuration
    {
        public string OutputFile { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string[] WebDavEndpoints { get; set; } = [];
        public int MaxNumberOfConnections { get; set; } = 5;
        public bool SplitContactWhenMultiplePhoneNumbers { get; set; } = false;
        public string? PingUrlWhenFinishedSuccessfully { get; set; }
    }
}
