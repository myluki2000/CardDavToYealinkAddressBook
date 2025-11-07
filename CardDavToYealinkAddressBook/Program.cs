using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Enums;
using FolkerKinzel.VCards.Models.Properties;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebDav;
using static CardDavToYealinkAddressBook.Program;

namespace CardDavToYealinkAddressBook;

internal class Program
{
    public static async Task Main(string[] args)
    {
        Configuration config = JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(args[0]))!;

        Console.WriteLine($"Connecting to server {config.Server}...");
        DateTime startTime = DateTime.Now;

        using WebDavClient webDav = new(new WebDavClientParams()
        {
            BaseAddress = new Uri(config.Server),
            Credentials = new NetworkCredential(config.Username, config.Password),
        });

        Console.WriteLine("Connected.");

        List<string> vcfUrisToFetch = [];
        ConcurrentBag<Contact> contacts = [];

        foreach (string reqUri in config.WebDavEndpoints)
        {
            PropfindResponse response = await webDav.Propfind(reqUri);

            if (!response.IsSuccessful)
                return;

            foreach (WebDavResource res in response.Resources)
            {
                if (res.Uri == reqUri || res.Uri == null)
                    continue;

                PropfindResponse bookResponse = await webDav.Propfind(res.Uri);
                if (!bookResponse.IsSuccessful)
                    continue;

                foreach (WebDavResource bookRes in bookResponse.Resources)
                {
                    if (bookRes.ContentType == null || !bookRes.ContentType.StartsWith("text/vcard"))
                        continue;

                    vcfUrisToFetch.Add(bookRes.Uri);
                }
            }
        }

        await Parallel.ForEachAsync(vcfUrisToFetch, 
            new ParallelOptions()
            {
                MaxDegreeOfParallelism = config.MaxNumberOfConnections
            },
            async (uri, token) =>
        {
            List<Contact> newContacts = await FetchContact(webDav, uri);
            foreach (Contact newContact in newContacts)
            {
                contacts.Add(newContact);
            }
        });

        Console.WriteLine("Finished fetching contacts. Generating phonebook xml file...");

        await using FileStream outputStream = File.Create(config.OutputFile);
        GenerateYealinkPhonebookXml(config, contacts.ToList(), outputStream);

        Console.WriteLine($"Saved phonebook xml file to {config.OutputFile}");

        Console.WriteLine($"Finished in {(DateTime.Now - startTime).TotalSeconds} seconds.");

        if (config.PingUrlWhenFinishedSuccessfully != null)
        {
            using HttpClient httpClient = new();
            await httpClient.GetAsync(config.PingUrlWhenFinishedSuccessfully);
        }
    }

    private static async Task<List<Contact>> FetchContact(WebDavClient webDav, string uri)
    {
        WebDavStreamResponse vcardResponse = await webDav.GetProcessedFile(uri);
        IReadOnlyList<VCard> vCards = Vcf.Deserialize(vcardResponse.Stream);
        
        List<Contact> contacts = [];

        foreach (VCard card in vCards)
        {
            if (card.DisplayNames?.FirstOrDefault() == null)
                continue;

            if (card.Phones?.FirstOrDefault() == null)
                continue;

            contacts.Add(new Contact()
            {
                Name = card.DisplayNames.First()!.Value,
                Phones = VCardPhonesToPhoneNumbers(card).ToList(),
            });
        }

        return contacts;
    }

    private static void GenerateYealinkPhonebookXml(Configuration config, List<Contact> contacts, Stream outputStream)
    {
        using StreamWriter writer = new(outputStream);
        writer.WriteLine("<YealinkIPPhoneDirectory>");

        foreach (Contact contact in contacts)
        {
            if (config.SplitContactWhenMultiplePhoneNumbers)
            {
                foreach (PhoneNumber number in contact.Phones)
                {
                    string namePostfix = string.Empty;

                    if (contact.Phones.Count > 1 && !string.IsNullOrEmpty(number.Type))
                    {
                        namePostfix = $" ({number.Type})";
                    }

                    WriteYealinkDirectoryEntry(writer, contact.Name + namePostfix, [ModifyPhoneNumber(config, number.Number)]);
                }
            }
            else
            {
                WriteYealinkDirectoryEntry(writer, contact.Name, contact.Phones.Select(x => ModifyPhoneNumber(config, x.Number)));
            }
        }
        writer.WriteLine("</YealinkIPPhoneDirectory>");
    }

    private static void WriteYealinkDirectoryEntry(StreamWriter writer, string name, IEnumerable<string> phoneNumbers)
    {
        writer.WriteLine("  <DirectoryEntry>");
        writer.WriteLine($"    <Name>{System.Security.SecurityElement.Escape(name)}</Name>");
        foreach (string number in phoneNumbers)
        {
            writer.WriteLine($"    <Telephone>{System.Security.SecurityElement.Escape(number)}</Telephone>");
        }
        writer.WriteLine("  </DirectoryEntry>");
    }

    private static IEnumerable<PhoneNumber> VCardPhonesToPhoneNumbers(VCard vCard)
    {
        if (vCard.Phones == null)
            yield break;

        foreach (TextProperty? phone in vCard.Phones)
        {
            if (phone == null)
                continue;

            string phoneType = phone.Parameters.PhoneType switch
            {
                Tel.Voice => "",
                Tel.Cell => "Mobil",
                _ => "",
            };

            if(phoneType.Length > 0)
                phoneType += " ";

            phoneType += phone.Parameters.PropertyClass switch
            {
                PCl.Home => "Privat",
                PCl.Work => "Geschäftl.",
                _ => "",
            };


            yield return new PhoneNumber()
            {
                Number = phone.Value,
                Type = phoneType,
            };
        }
    }

    private static string ModifyPhoneNumber(Configuration config, string phoneNumber)
    {
        if (config.CountryCode != null)
        {
            if (phoneNumber.StartsWith("00"))
            {
                return "+" + phoneNumber.Substring("00".Length);
            }
            
            if (Regex.IsMatch(phoneNumber, @"^0[1-9][0-9]*$"))
            {
                return "+" + config.CountryCode + phoneNumber.Substring(1);
            }
        }

        return phoneNumber;
    }

    public class Contact
    {
        public required string Name { get; set; }
        public List<PhoneNumber> Phones { get; set; } = [];
    }

    public class PhoneNumber
    {
        public string Number { get; set; }
        public string? Type { get; set; }
    }
}