using System;
using System.IO;
using System.Text;

namespace CommNetLunaBridge
{
    public sealed class FundingCommit
    {
        public NetworkState State { get; set; }
        public string Scenario { get; set; }
    }
    // Write-ahead record couples a CNC revision/receipt to its career charge.
    // Recovery runs before clients are admitted; a retry cannot charge twice.
    public sealed class FundingJournal
    {
        private readonly AtomicStore store;
        private readonly string journal, fundingPath;
        public FundingJournal(AtomicStore store, string fundingPath)
        { this.store = store; this.fundingPath = fundingPath; journal = store.Path + ".funding-commit"; }
        public bool Pending => File.Exists(journal);
        public void Commit(NetworkState state, string scenario)
        {
            Authority.Validate(state);
            Authority.Require(state.Funding != null, "missing-funding-debit");
            var tree = ConfigTree.Parse(scenario);
            Authority.Require(ConfigTree.Number(tree.Get("funds")) == state.Funding.Before, "funding-changed");
            tree.Set("funds", state.Funding.After);
            WriteAtomic(journal, Wire.Encode(new FundingCommit { State = state, Scenario = tree.ToString() }));
            Recover();
        }
        public FundingCommit Recover()
        {
            if (!Pending) return null;
            var data = File.ReadAllBytes(journal);
            var commit = Wire.Decode<FundingCommit>(data, data.Length);
            Authority.Validate(commit.State);
            Authority.Require(commit.State.Funding != null && ConfigTree.Number(ConfigTree.Parse(commit.Scenario).Get("funds")) == commit.State.Funding.After, "invalid-funding-journal");
            if (File.Exists(store.Path))
            {
                var current = store.Load();
                Authority.Require(current.Epoch == commit.State.Epoch && (current.Revision == commit.State.Revision - 1 || Wire.Same(current, commit.State)), "funding-journal-conflict");
            }
            // If interrupted after either write, the same journal completes both.
            store.Save(commit.State);
            WriteAtomic(fundingPath, Encoding.UTF8.GetBytes(commit.Scenario));
            File.Delete(journal);
            return commit;
        }
        private static void WriteAtomic(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
    }
}
