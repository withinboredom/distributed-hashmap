using System.Threading.Tasks;

namespace IntegrationTest
{
    internal interface ICommand
    {
        public string Command { get; }

        public Task<int> Run(string[] args);
    }
}