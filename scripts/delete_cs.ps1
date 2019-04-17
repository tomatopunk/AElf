$p=pwd
echo '---------------------------------------------------'
echo 'find-------------------------------------------------'
Get-ChildItem $p\Protobuf\Generated -recurse *.g.cs
Get-ChildItem $p\Protobuf\Generated -recurse *.g.gs
echo 'delete----------------------------------------------'
Get-ChildItem $p\Protobuf\Generated -recurse *.g.cs | rm
Get-ChildItem $p\Protobuf\Generated -recurse *.g.gs | rm
echo 'delete after find------------------  ---------------'
Get-ChildItem $p\Protobuf\Generated -recurse *.g.cs
Get-ChildItem $p\Protobuf\Generated -recurse *.g.gs
echo '---------------------------------------------------'
