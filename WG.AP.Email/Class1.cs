using WG.AP.Core.Abstractions;

namespace WG.AP.Email
{
    public class Class1
    {
        private readonly IMailSource _mailSource;

        public Class1(IMailSource mailSource)
        {
            _mailSource = mailSource;
        }

        //public async Task<string> GetLatestEmails(DateTime dateTimeLastProcessedCreatedOnMailbox)
        //{
        //    await _mailSource.GetMessageAsync(dateTimeLastProcessedCreatedOnMailbox);
        //    await _mailSource.MoveMessageToFolderAsync("messageId", MailDestinationFolder.Processed);
        //    Array<string> messageIds = await _mailSource.GetMessageIdsAsync();

        //    messageIds.AsParallel(10).ForEach(messageId => { }
        //}
    }
}
