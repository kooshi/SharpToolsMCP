using SharpTools.Tools.Services;

namespace SharpTools.Tools.Tests.Services {
    public class FileChangeListenerTests {
        [Theory]
        [InlineData("c:\\root\\obj", true)]
        [InlineData("c:\\root\\.git", true)]
        [InlineData("c:\\root\\bin", true)]
        [InlineData("c:\\root\\BIN", true)]
        [InlineData("c:\\root\\foo", false)]
        [InlineData("c:\\root\\nested\\obj", true)]
        [InlineData("c:\\root\\nested\\.git", true)]
        [InlineData("c:\\root\\nested\\bin", true)]
        [InlineData("c:\\root\\nested\\BIN", true)]
        [InlineData("c:\\root\\nested\\foo", false)]
        public void IsPathIgnored_ignores_git_bin_obj(string path, bool isIgnored) {
            Assert.Equal(isIgnored, FileChangeListener.IsPathIgnored("c:\\root", path, ['\\']));
        }
    }
}