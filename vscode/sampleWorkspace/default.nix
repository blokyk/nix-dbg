#break "hello world!"
let
  f = arg: "hello ${break arg}";
  a = "world";
in
  break (f a)