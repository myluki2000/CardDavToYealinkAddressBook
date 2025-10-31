
using System.CommandLine;
using System.Net;
using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Enums;
using FolkerKinzel.VCards.Models.Properties;
using WebDav;

namespace CardDavToYealinkAddressBook;

internal class Program
{
    public static async Task Main(string[] args)
    {
        Argument<string> outputFileArg = new("output-file")
        {
            Description = "The output file path for the Yealink phonebook XML file."
        };

        Option<string> serverOption = new("--server", "-s")
        {
            Description = "The CardDAV server URL.",
        };

        Option<string> usernameOption = new("--username", "-u")
        {
            Description = "The username for CardDAV server authentication.",
        };

        Option<string> passwordOption = new("--password", "-p")
        {
            Description = "The password for CardDAV server authentication.",
        };

        Option<string> webDavEndpointOption = new("--webdav-endpoint", "-e")
        {
            Description = "The WebDAV endpoint path relative to the server URL.",
        };

        RootCommand rootCommand = new();
        rootCommand.Arguments.Add(outputFileArg);
        rootCommand.Options.Add(serverOption);
        rootCommand.Options.Add(usernameOption);
        rootCommand.Options.Add(passwordOption);
        ParseResult parseResult = rootCommand.Parse(args);

        using WebDav.WebDavClient webDav = new(new WebDavClientParams()
        {
            BaseAddress = new Uri(parseResult.GetRequiredValue(serverOption)),
            Credentials = new NetworkCredential(parseResult.GetRequiredValue(usernameOption), parseResult.GetRequiredValue(passwordOption)),
        });
    
        string reqUri = parseResult.GetRequiredValue(webDavEndpointOption);
        PropfindResponse response = await webDav.Propfind(reqUri);

        if (!response.IsSuccessful)
            return;

        List<Contact> contacts = [];

        foreach (WebDavResource res in response.Resources)
        {
            if (res.Uri == reqUri || res.Uri == null)
                continue;
            
            PropfindResponse bookResponse = await webDav.Propfind(res.Uri);
            if (!bookResponse.IsSuccessful)
                continue;

            foreach (WebDavResource bookRes in ((PropfindResponse)bookResponse).Resources)
            {
                if (bookRes.ContentType == null || !bookRes.ContentType.StartsWith("text/vcard"))
                    continue;

                WebDavStreamResponse vcardResponse = await webDav.GetProcessedFile(bookRes.Uri);
                IReadOnlyList<VCard> vCards = Vcf.Deserialize(vcardResponse.Stream);
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
            }
        }

        await using FileStream outputStream = File.Create(parseResult.GetRequiredValue(outputFileArg));
        GenerateYealinkPhonebookXml(contacts, outputStream);
    }

    private static void GenerateYealinkPhonebookXml(List<Contact> contacts, Stream outputStream)
    {
        using StreamWriter writer = new(outputStream);
        writer.WriteLine("<YealinkIPPhoneDirectory>");

        foreach (Contact contact in contacts)
        {
            writer.WriteLine("  <DirectoryEntry>");
            writer.WriteLine($"    <Name>{System.Security.SecurityElement.Escape(contact.Name)}</Name>");

            foreach (PhoneNumber number in contact.Phones)
            {
                writer.WriteLine(
                    $"    <Telephone>{System.Security.SecurityElement.Escape(number.Number)}</Telephone>");
            }

            writer.WriteLine("  </DirectoryEntry>");
        }

        writer.WriteLine("</YealinkIPPhoneDirectory>");
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