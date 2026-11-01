import { parseRepo } from "./AddRepo";

it.each([
  ["acme/widgets", "acme", "widgets"],
  ["https://github.com/acme/widgets", "acme", "widgets"],
  ["https://github.com/acme/widgets.git", "acme", "widgets"],
  ["https://github.com/acme/widgets/tree/main/src", "acme", "widgets"],
  ["git@github.com:acme/my.repo.git", "acme", "my.repo"],
])("parses %s", (input, owner, repo) => {
  expect(parseRepo(input)).toEqual({ owner, repo });
});

it.each(["", "acme", "https://gitlab.com/acme/widgets", "a b/c"])("rejects %j", (input) => {
  expect(parseRepo(input)).toBeNull();
});
